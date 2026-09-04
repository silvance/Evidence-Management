using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Documents;
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

public enum DocumentHashStatus { Match = 1, Mismatch = 2, Missing = 3 }

/// <summary>
/// One source document's integrity, for the room report. Identifiers and statuses only - no
/// filename, case number, document number or content (AUD-022, IAM-009).
/// </summary>
public sealed record DocumentIntegrityRow(
    int DocumentId, int EvidenceRoomId, DocumentHashStatus OriginalHash, int PagesChecked, int PagesMismatched);

public sealed record IntegrityReport(
    int EvidenceRoomId,
    DateTimeOffset RanAtUtc,
    int ItemsChecked,
    IReadOnlyList<ItemIntegrityRow> Findings,
    int DocumentsChecked = 0,
    IReadOnlyList<DocumentIntegrityRow>? DocumentFindings = null)
{
    /// <summary>Source documents whose stored bytes no longer hash to what was recorded, or are missing (AUD-022). Distinct from both counts below.</summary>
    public int DocumentIntegrityFailures => DocumentFindings?.Count ?? 0;

    /// <summary>Items whose history itself does not verify (AUD-008). An incident, not a repair.</summary>
    public int EventChainFailures => Findings.Count(f => !f.Chain.IsIntact);

    /// <summary>Items whose history verifies but whose stored summary disagrees with it (AUD-021).</summary>
    public int SnapshotMismatches => Findings.Count(f => f.Chain.IsIntact && !f.Snapshot.IsConsistent);

    public bool IsIntact => Findings.Count == 0 && DocumentIntegrityFailures == 0;
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
    private readonly Emc.Application.Documents.ISourceDocumentStore? _store;

    public IntegrityVerificationService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        IAuditRecorder audit,
        IClock clock,
        Emc.Application.Documents.ISourceDocumentStore? store = null)
    {
        _db = db;
        _authorization = authorization;
        _audit = audit;
        _clock = clock;
        _store = store;
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

        // AUD-022. Source documents: the bytes under each key must still hash to what was recorded
        // at receipt. Reported apart from event-chain and snapshot findings - it is a third kind of
        // thing: the record is intact, the companion copy it points at is not. Identifiers only.
        var documentFindings = new List<DocumentIntegrityRow>();
        var documentsChecked = 0;

        if (_store is not null)
        {
            var documents = await _db.SourceDocuments.AsNoTracking()
                .Where(d => d.EvidenceRoomId == evidenceRoomId)
                .Select(d => new { d.Id, d.EvidenceRoomId, d.StorageKey, d.Sha256 })
                .ToListAsync(ct);
            var documentIds = documents.Select(d => d.Id).ToList();

            // Every page of every SUCCESSFUL render run: the pages a person may have looked at, on
            // any run, must still be the bytes recorded when they were rendered.
            var renderedPages = await _db.DocumentRenderRuns.AsNoTracking()
                .Where(r => documentIds.Contains(r.SourceDocumentId) && r.Outcome == RenderRunOutcome.Succeeded)
                .Join(_db.DocumentRenderPages.AsNoTracking(), r => r.Id, p => p.RenderRunId, (r, p) => new { r.SourceDocumentId, p.StorageKey, p.Sha256 })
                .ToListAsync(ct);

            foreach (var d in documents)
            {
                documentsChecked++;
                var actual = await _store.ComputeSha256Async(d.StorageKey, ct);
                var status = actual is null ? DocumentHashStatus.Missing
                    : string.Equals(actual, d.Sha256, StringComparison.Ordinal) ? DocumentHashStatus.Match
                    : DocumentHashStatus.Mismatch;

                var pagesMismatched = 0;
                var pages = renderedPages.Where(p => p.SourceDocumentId == d.Id).ToList();
                foreach (var p in pages)
                {
                    var pageHash = await _store.ComputeSha256Async(p.StorageKey, ct);
                    if (!string.Equals(pageHash, p.Sha256, StringComparison.Ordinal))
                    {
                        pagesMismatched++;
                    }
                }

                if (status != DocumentHashStatus.Match || pagesMismatched > 0)
                {
                    documentFindings.Add(new DocumentIntegrityRow(d.Id, d.EvidenceRoomId, status, pages.Count, pagesMismatched));
                }
            }
        }

        var report = new IntegrityReport(evidenceRoomId, _clock.UtcNow, items.Count, findings, documentsChecked, documentFindings);

        _audit.Record(
            AuditEventType.IntegrityVerificationRun, "EvidenceRoom", evidenceRoomId.ToString(),
            newValue: $"{report.ItemsChecked} items checked; {report.EventChainFailures} event-chain "
                      + $"failures; {report.SnapshotMismatches} snapshot mismatches; "
                      + $"{report.DocumentsChecked} source documents checked; {report.DocumentIntegrityFailures} document-integrity failures.",
            succeeded: report.IsIntact);

        await _db.SaveChangesAsync(ct);
        return OperationResult<IntegrityReport>.Success(report);
    }
}
