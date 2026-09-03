using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Integrity;

/// <summary>
/// One item's result in a room-wide report. Carries identifiers and problems only - never a
/// description, serial number or custody party - so that the report can be run by the
/// application administrator, who holds VerifyIntegrity but no evidence-read permission
/// (IAM-009, IAM-017).
/// </summary>
public sealed record ItemIntegrityRow(
    int ItemId,
    int VoucherId,
    string VoucherIdentifier,
    int ItemNumber,
    ChainVerificationResult Chain,
    SnapshotVerificationResult Snapshot)
{
    public bool IsIntact => Chain.IsIntact && Snapshot.IsConsistent;
}

public sealed record IntegrityReport(
    int EvidenceRoomId,
    DateTimeOffset RanAtUtc,
    int ItemsChecked,
    IReadOnlyList<ItemIntegrityRow> Findings)
{
    /// <summary>Items whose history itself does not verify (AUD-008). An incident, not a repair.</summary>
    public int EventChainFailures => Findings.Count(f => !f.Chain.IsIntact);

    /// <summary>Items whose history verifies but whose stored summary disagrees with it (AUD-021).</summary>
    public int SnapshotMismatches => Findings.Count(f => f.Chain.IsIntact && !f.Snapshot.IsConsistent);

    public bool IsIntact => Findings.Count == 0;
}

public interface IIntegrityVerificationService
{
    Task<OperationResult<IntegrityReport>> VerifyEvidenceRoomAsync(int evidenceRoomId, CancellationToken ct = default);
}

/// <summary>
/// Verifies every item in an evidence room: the per-item hash chain (AUD-008) and the item's
/// stored summary against that chain (AUD-021), reported as two distinct kinds of finding.
/// The run itself is audit logged (AuditEventType.IntegrityVerificationRun).
/// </summary>
public sealed class IntegrityVerificationService : IIntegrityVerificationService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;

    public IntegrityVerificationService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        IAuditRecorder audit,
        IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _audit = audit;
        _clock = clock;
    }

    public async Task<OperationResult<IntegrityReport>> VerifyEvidenceRoomAsync(
        int evidenceRoomId, CancellationToken ct = default)
    {
        var decision = await _authorization.AuthorizeAsync(EmcPermissions.VerifyIntegrity, evidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(
                AuditEventType.PermissionDenied, "EvidenceRoom", evidenceRoomId.ToString(),
                reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return OperationResult<IntegrityReport>.Failure(decision.Reason!, decision.RequirementId);
        }

        // Identifiers and summary fields only. The item's description is deliberately not
        // selected: this report may be run by someone who must not read evidence content.
        var items = await _db.EvidenceItems
            .AsNoTracking()
            .Where(i => i.Voucher!.EvidenceRoomId == evidenceRoomId)
            .Include(i => i.Voucher!).ThenInclude(v => v.DocumentNumberAssignments)
            .OrderBy(i => i.VoucherId).ThenBy(i => i.ItemNumber)
            .ToListAsync(ct);

        var itemIds = items.Select(i => i.Id).ToList();

        var eventsByItem = (await _db.ItemEvents
                .AsNoTracking()
                .Where(e => itemIds.Contains(e.EvidenceItemId))
                .ToListAsync(ct))
            .GroupBy(e => e.EvidenceItemId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ItemEvent>)g.OrderBy(e => e.SequenceNumber).ToList());

        var findings = new List<ItemIntegrityRow>();

        foreach (var item in items)
        {
            var events = eventsByItem.GetValueOrDefault(item.Id, []);
            var result = ItemIntegrityResult.Of(item, events);

            if (!result.IsIntact)
            {
                findings.Add(new ItemIntegrityRow(
                    item.Id, item.VoucherId, item.Voucher!.DisplayIdentifier, item.ItemNumber,
                    result.Chain, result.Snapshot));
            }
        }

        var report = new IntegrityReport(evidenceRoomId, _clock.UtcNow, items.Count, findings);

        _audit.Record(
            AuditEventType.IntegrityVerificationRun, "EvidenceRoom", evidenceRoomId.ToString(),
            newValue: $"{report.ItemsChecked} items checked; {report.EventChainFailures} event-chain "
                      + $"failures; {report.SnapshotMismatches} snapshot mismatches.",
            succeeded: report.IsIntact);

        await _db.SaveChangesAsync(ct);
        return OperationResult<IntegrityReport>.Success(report);
    }
}
