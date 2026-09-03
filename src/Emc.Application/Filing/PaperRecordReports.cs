using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Filing;

/// <summary>An advisory about the paper record and the companion record disagreeing. Never a state change; a person looks (PDC-001).</summary>
public sealed record PaperConsistencyAdvisory(string Code, string Regulation, string Message);

/// <summary>
/// Cross-checks between the PHYSICAL DA Form 4137 record (where the original and its copies are)
/// and the DIGITAL companion (item states, companion copies, verified scans). Every result is an
/// advisory: the software cannot know which side is wrong, and under AR 195-5 2-5c the paper
/// and the ledger are authoritative, so it says what disagrees and to whom, and changes nothing.
/// </summary>
public interface IPhysicalDigitalConsistencyService
{
    Task<IReadOnlyList<PaperConsistencyAdvisory>> GetAdvisoriesAsync(int voucherId, CancellationToken ct = default);
}

public sealed class PhysicalDigitalConsistencyService : IPhysicalDigitalConsistencyService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly IClock _clock;

    public PhysicalDigitalConsistencyService(IEmcDbContext db, IEvidenceAuthorizationService authorization, IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _clock = clock;
    }

    public async Task<IReadOnlyList<PaperConsistencyAdvisory>> GetAdvisoriesAsync(int voucherId, CancellationToken ct = default)
    {
        var voucher = await _db.EvidenceVouchers.AsNoTracking().Include(v => v.Items).Include(v => v.DocumentNumberAssignments).FirstOrDefaultAsync(v => v.Id == voucherId, ct);
        if (voucher is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewVoucher, voucher.EvidenceRoomId, ct)).IsAllowed)
        {
            return [];
        }

        var paper = await _db.PhysicalVoucherDocuments.AsNoTracking().FirstOrDefaultAsync(p => p.VoucherId == voucherId, ct);
        var scans = await _db.SourceDocuments.AsNoTracking().Where(d => d.VoucherId == voucherId).ToListAsync(ct);
        return Compute(voucher, paper, scans, _clock.UtcNow);
    }

    internal static IReadOnlyList<PaperConsistencyAdvisory> Compute(Emc.Domain.Cases.EvidenceVoucher voucher, PhysicalVoucherDocument? paper, IReadOnlyList<SourceDocument> scans, DateTimeOffset now)
    {
        var advisories = new List<PaperConsistencyAdvisory>();
        var lines = voucher.CurrentFormLines.ToList();
        var statuses = lines.Select(i => i.AccountabilityStatus).ToList();
        var accepted = voucher.HasOfficialDocumentNumber;
        var derived = voucher.DerivedStatus;

        if (!accepted)
        {
            if (paper is not null && paper.OriginalStatus != PhysicalOriginalStatus.NotYetFiled)
            {
                advisories.Add(new("PDC-010", "2-4c", "The paper record says the original is filed, but no official document number is recorded on the companion. The custodian assigns the number at acceptance and the original is filed after it (2-4c, 2-4f(1))."));
            }

            return advisories;
        }

        // Paper original vs item states.
        var anyReleased = statuses.Any(s => s == AccountabilityStatus.TemporarilyReleased);
        var allTerminalOrTransferred = lines.Count > 0 && statuses.All(s => s == AccountabilityStatus.PermanentlyTransferred);
        var status = paper?.OriginalStatus ?? PhysicalOriginalStatus.NotYetFiled;

        if (paper is null || status == PhysicalOriginalStatus.NotYetFiled)
        {
            advisories.Add(new("PDC-001", "2-4d, 2-4f(1)", "The voucher is accepted but the paper record does not say where the original DA Form 4137 is filed. Record its filing in the active file."));
        }

        if (anyReleased && status is PhysicalOriginalStatus.FiledActive or PhysicalOriginalStatus.FiledInactive)
        {
            advisories.Add(new("PDC-002", "2-7a, 2-4f(2)", "An item is on temporary release, but the paper record says the original DA Form 4137 is in the file. The original accompanies the evidence and a copy goes to the suspense folder; record the release of the original, or check the item's state."));
        }

        if (!anyReleased && status == PhysicalOriginalStatus.AccompanyingTemporaryRelease)
        {
            advisories.Add(new("PDC-003", "2-7a, 2-4f(2)", "The paper record says the original DA Form 4137 is out with the evidence, but no item on this voucher is on temporary release. Record the return of the original, or check the items' states."));
        }

        if (derived == VoucherDerivedStatus.Inactive && status is PhysicalOriginalStatus.FiledActive or PhysicalOriginalStatus.AccompanyingTemporaryRelease or PhysicalOriginalStatus.SentForDispositionApproval)
        {
            advisories.Add(new("PDC-004", "2-4h", "Every item is disposed of, so the voucher is inactive, but the paper record says the original is still in the active file or out. File it in the inactive file labelled with the month and year."));
        }

        if (derived != VoucherDerivedStatus.Inactive && status == PhysicalOriginalStatus.FiledInactive && !allTerminalOrTransferred)
        {
            advisories.Add(new("PDC-005", "2-4h", "The paper record says the original is in the inactive file, but the companion still carries items that are not disposed of. A voucher becomes inactive only when all items are disposed of."));
        }

        if (allTerminalOrTransferred && status is not (PhysicalOriginalStatus.TransferredToGainingRoom or PhysicalOriginalStatus.FiledInactive))
        {
            advisories.Add(new("PDC-006", "2-7g, 2-4d", "Every item is permanently transferred, but the paper record does not say the original went to the gaining unit with a copy in the inactive file."));
        }

        if (paper is { HoldsCopyOnly: true, CopyReason: CopyRetentionReason.None })
        {
            advisories.Add(new("PDC-007", "2-4g", "The paper record holds a copy only, without saying why (record of trial, external agency, or other unavailability)."));
        }

        if (paper is not null && paper.RetentionStatusAt(now) == PaperRetentionStatus.EligibleForDestruction)
        {
            advisories.Add(new("PDC-008", "2-4h", $"The inactive DA Form 4137 has been inactive since {paper.InactiveSinceUtc:dd MMM yy} and is eligible for destruction. Destruction is confirmed by a person, on the paper dashboard; nothing digital is destroyed (DEC-07)."));
        }

        // Companion copies vs paper.
        if (scans.Count == 0)
        {
            advisories.Add(new("PDC-020", "companion (DOC-001)", "No companion copy of the DA Form 4137 has been received for this accepted voucher. Optional: the paper is the record; a companion copy lets it be verified and reconciled."));
        }

        foreach (var scan in scans.Where(s => s.Provenance == ScanProvenance.PhysicalOriginal && s.DocumentType == SourceDocumentType.DaForm4137))
        {
            var originalGone = status is PhysicalOriginalStatus.TransferredToGainingRoom or PhysicalOriginalStatus.PartOfRecordOfTrial or PhysicalOriginalStatus.WithExternalAgency or PhysicalOriginalStatus.Destroyed;
            var leftAt = paper?.InactiveSinceUtc;
            if (originalGone && (leftAt is null || scan.ReceivedAtUtc > leftAt))
            {
                advisories.Add(new("PDC-021", "2-4g", $"Companion copy {scan.Id} is recorded as a scan of the PHYSICAL ORIGINAL received {scan.ReceivedAtUtc:dd MMM yy}, but the paper record says the original is {status}. Check the scan's provenance."));
            }
        }

        var distinctHashes = scans.Where(s => s.DocumentType == SourceDocumentType.DaForm4137).Select(s => s.Sha256).Distinct().Count();
        if (distinctHashes > 1)
        {
            advisories.Add(new("PDC-022", "companion (DOC-011)", $"{distinctHashes} different companion copies of the DA Form 4137 are on record for this voucher. Later scans are expected as the paper changes (chain of custody entries); confirm the latest reflects the current paper."));
        }

        return advisories;
    }
}

public sealed record RetentionRow(
    int VoucherId, string VoucherIdentifier, VoucherDerivedStatus VoucherStatus, PhysicalOriginalStatus OriginalStatus, bool HoldsCopyOnly, CopyRetentionReason CopyReason,
    string? ContainerLabel, DateTimeOffset? InactiveSinceUtc, DateTimeOffset? DestructionEligibleAtUtc, PaperRetentionStatus RetentionStatus, DateTimeOffset? DestructionConfirmedAtUtc);

public sealed record ContainerLoadRow(FileContainerRow Container, int Capacity, bool OverCapacity);

/// <summary>The evidence room's paper DA Form 4137 files as a dashboard: Active, Suspense, Inactive (Retain / Eligible / Destruction confirmed).</summary>
public sealed record RetentionDashboardView(
    int EvidenceRoomId,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<RetentionRow> Active,
    IReadOnlyList<RetentionRow> Suspense,
    IReadOnlyList<RetentionRow> InactiveRetain,
    IReadOnlyList<RetentionRow> InactiveEligibleForDestruction,
    IReadOnlyList<RetentionRow> InactiveDestructionConfirmed,
    IReadOnlyList<RetentionRow> Unfiled,
    IReadOnlyList<ContainerLoadRow> Containers)
{
    public int AcceptedVouchersWithoutPaperRecord => Unfiled.Count;
}

public interface IRetentionDashboardService
{
    /// <summary>Null when the room is absent or the caller may not view its vouchers.</summary>
    Task<RetentionDashboardView?> GetAsync(int evidenceRoomId, CancellationToken ct = default);
}

public sealed class RetentionDashboardService : IRetentionDashboardService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly IPhysicalDocumentService _physical;
    private readonly IClock _clock;

    public RetentionDashboardService(IEmcDbContext db, IEvidenceAuthorizationService authorization, IPhysicalDocumentService physical, IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _physical = physical;
        _clock = clock;
    }

    public async Task<RetentionDashboardView?> GetAsync(int evidenceRoomId, CancellationToken ct = default)
    {
        if (!await _db.EvidenceRooms.AsNoTracking().AnyAsync(r => r.Id == evidenceRoomId, ct)
            || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewVoucher, evidenceRoomId, ct)).IsAllowed)
        {
            return null;
        }

        var now = _clock.UtcNow;
        var vouchers = await _db.EvidenceVouchers.AsNoTracking().Include(v => v.Items).Include(v => v.DocumentNumberAssignments)
            .Where(v => v.EvidenceRoomId == evidenceRoomId).ToListAsync(ct);
        var papers = await _db.PhysicalVoucherDocuments.AsNoTracking().Where(p => p.EvidenceRoomId == evidenceRoomId).ToDictionaryAsync(p => p.VoucherId, ct);
        var containers = await _physical.GetContainersAsync(evidenceRoomId, ct);
        var labels = containers.ToDictionary(c => c.Id, c => c.Label);

        var rows = new List<RetentionRow>();
        var unfiled = new List<RetentionRow>();
        foreach (var v in vouchers.Where(v => v.HasOfficialDocumentNumber).OrderBy(v => v.DisplayIdentifier))
        {
            if (!papers.TryGetValue(v.Id, out var p) || p.OriginalStatus == PhysicalOriginalStatus.NotYetFiled)
            {
                unfiled.Add(new RetentionRow(v.Id, v.DisplayIdentifier, v.DerivedStatus, PhysicalOriginalStatus.NotYetFiled, false, CopyRetentionReason.None, null, null, null, PaperRetentionStatus.Retain, null));
                continue;
            }

            var containerId = p.InactiveContainerId ?? p.SuspenseCopyContainerId ?? p.OriginalContainerId;
            rows.Add(new RetentionRow(v.Id, v.DisplayIdentifier, v.DerivedStatus, p.OriginalStatus, p.HoldsCopyOnly, p.CopyReason,
                containerId is int id && labels.TryGetValue(id, out var label) ? label : null,
                p.InactiveSinceUtc, p.DestructionEligibleAtUtc, p.RetentionStatusAt(now), p.DestructionConfirmedAtUtc));
        }

        bool Inactive(RetentionRow r) => r.InactiveSinceUtc is not null;
        var active = rows.Where(r => !Inactive(r) && r.OriginalStatus == PhysicalOriginalStatus.FiledActive).ToList();
        var suspense = rows.Where(r => !Inactive(r) && r.OriginalStatus is PhysicalOriginalStatus.AccompanyingTemporaryRelease or PhysicalOriginalStatus.SentForDispositionApproval).ToList();
        var inactive = rows.Where(Inactive).ToList();

        return new RetentionDashboardView(
            evidenceRoomId, now, active, suspense,
            inactive.Where(r => r.RetentionStatus == PaperRetentionStatus.Retain).ToList(),
            inactive.Where(r => r.RetentionStatus == PaperRetentionStatus.EligibleForDestruction).ToList(),
            inactive.Where(r => r.RetentionStatus == PaperRetentionStatus.DestructionConfirmed).ToList(),
            unfiled,
            containers.Select(c => new ContainerLoadRow(c, c.Kind == PhysicalFileKind.Active4137File ? PhysicalFileContainer.ActiveFileVoucherCapacity : 0,
                c.Kind == PhysicalFileKind.Active4137File && c.VouchersFiled > PhysicalFileContainer.ActiveFileVoucherCapacity)).ToList());
    }
}
