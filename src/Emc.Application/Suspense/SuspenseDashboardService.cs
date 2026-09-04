using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Emc.Domain.Suspense;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Suspense;

/// <summary>One open release (or, for the PENDING DISPOSITION APPROVAL folder, one original out for approval) as the dashboard shows it.</summary>
public sealed record SuspenseRow(
    int? ReleaseId,
    int VoucherId,
    string VoucherIdentifier,
    SuspenseCategory Category,
    string HeldBy,
    CustodyPartyKind? HeldByKind,
    string? LaboratoryName,
    PaperCopyKind? PaperAccompanying,
    DateTimeOffset OutSinceLocal,
    int DaysOut,
    int ItemsOut,
    string ItemNumbers,
    DateTimeOffset? LastContactAtUtc,
    int? DaysSinceLastContact,
    DateTimeOffset? ExpectedFollowUpLocal,
    bool FollowUpDue,
    bool ExceedsLocalReviewThreshold,
    string SuspenseFolderLabel);

/// <summary>A disagreement between the release records and the rest of the companion record, for a person to look at. Never a state change.</summary>
public sealed record SuspenseConsistencyAdvisory(string Code, string Regulation, int? VoucherId, string? VoucherIdentifier, string Message);

public sealed record SuspenseDashboardView(
    int EvidenceRoomId,
    DateTimeOffset AsOfUtc,
    int LocalReviewThresholdDays,
    IReadOnlyList<SuspenseRow> Usacil,
    IReadOnlyList<SuspenseRow> Adjudication,
    IReadOnlyList<SuspenseRow> PendingDispositionApproval,
    IReadOnlyList<SuspenseRow> RecentlyClosed,
    IReadOnlyList<SuspenseConsistencyAdvisory> Advisories)
{
    public int OpenReleases => Usacil.Count + Adjudication.Count;
    public int ExceedingLocalThreshold => Usacil.Count(r => r.ExceedsLocalReviewThreshold) + Adjudication.Count(r => r.ExceedsLocalReviewThreshold) + PendingDispositionApproval.Count(r => r.ExceedsLocalReviewThreshold);
    public int FollowUpsDue => Usacil.Count(r => r.FollowUpDue) + Adjudication.Count(r => r.FollowUpDue);
}

public interface ISuspenseDashboardService
{
    Task<SuspenseDashboardView?> GetAsync(int evidenceRoomId, CancellationToken ct = default);
}

/// <summary>
/// The suspense dashboard (SUSP-006, 3-1a(4)): what is out of the room, with whom, for how long,
/// when it was last chased and when the custodian meant to chase it next - by the regulation's
/// three folders. Days out is a count. The one threshold shown is the LOCAL management review
/// threshold from SystemConfiguration, and it is labelled as local (SUSP-004): AR 195-5 sets no
/// number of days; it requires "reasonable and adequate contact" (2-7a) and that evidence not be
/// out "for an excessive period" (2-7b, 3-1a(4)), which the inspector judges. Read-only.
///
/// The advisories (SUSP-017) cross-check the release records against item states, the paper
/// record, the custody chain and the folders, and change nothing.
/// </summary>
public sealed class SuspenseDashboardService : ISuspenseDashboardService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly IClock _clock;

    public SuspenseDashboardService(IEmcDbContext db, IEvidenceAuthorizationService authorization, IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _clock = clock;
    }

    public async Task<SuspenseDashboardView?> GetAsync(int evidenceRoomId, CancellationToken ct = default)
    {
        if (!await _db.EvidenceRooms.AsNoTracking().AnyAsync(r => r.Id == evidenceRoomId, ct)
            || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewVoucher, evidenceRoomId, ct)).IsAllowed)
        {
            return null;
        }

        var now = _clock.UtcNow;
        var threshold = (await _db.SystemConfigurations.AsNoTracking().Select(c => (int?)c.LocalSuspenseReviewThresholdDays).FirstOrDefaultAsync(ct)) ?? 60;

        var releases = await _db.TemporaryReleases.AsNoTracking()
            .Include(r => r.ReceivedBy).Include(r => r.Items).Include(r => r.Contacts)
            .Where(r => r.EvidenceRoomId == evidenceRoomId)
            .ToListAsync(ct);
        var voucherIds = releases.Select(r => r.VoucherId).Distinct().ToList();
        var vouchers = await _db.EvidenceVouchers.AsNoTracking().Include(v => v.Items).Include(v => v.DocumentNumberAssignments)
            .Where(v => v.EvidenceRoomId == evidenceRoomId).ToListAsync(ct);
        var byVoucher = vouchers.ToDictionary(v => v.Id);
        var containers = await _db.PhysicalFileContainers.AsNoTracking().Where(c => c.EvidenceRoomId == evidenceRoomId).ToListAsync(ct);
        var labels = containers.ToDictionary(c => c.Id, c => c.Label);
        var papers = await _db.PhysicalVoucherDocuments.AsNoTracking().Include(p => p.Events).Where(p => p.EvidenceRoomId == evidenceRoomId).ToListAsync(ct);

        SuspenseRow Row(TemporaryRelease r)
        {
            var voucher = byVoucher.GetValueOrDefault(r.VoucherId);
            var outSince = r.ReleasedAtLocal;
            var end = r.IsOpen ? now : (r.ClosedAtUtc ?? now);
            var days = Math.Max(0, (int)Math.Floor((end - r.ReleasedAtUtc).TotalDays));
            var lastContact = r.LastContactAtUtc;
            var followUp = r.ExpectedFollowUpLocal;
            return new SuspenseRow(
                r.Id, r.VoucherId, voucher?.DisplayIdentifier ?? $"voucher {r.VoucherId}", r.Category,
                r.ReceivedBy.DisplayName, r.ReceivedBy.Kind, r.Laboratory?.LaboratoryName, r.PaperAccompanying,
                outSince, days, r.ItemsOut,
                string.Join(", ", r.Items.Where(i => !r.IsOpen || i.Status == TemporaryReleaseItemStatus.Out).OrderBy(i => i.ItemNumber).Select(i => i.ItemNumber)),
                lastContact, lastContact is null ? null : Math.Max(0, (int)Math.Floor((now - lastContact.Value).TotalDays)),
                followUp, r.IsOpen && followUp is not null && followUp.Value.ToUniversalTime() <= now,
                r.IsOpen && days > threshold,
                labels.GetValueOrDefault(r.SuspenseFolderContainerId, "(folder)"));
        }

        var open = releases.Where(r => r.IsOpen).Select(Row).OrderByDescending(r => r.DaysOut).ToList();
        var closed = releases.Where(r => !r.IsOpen && r.ClosedAtUtc >= now.AddDays(-30)).Select(Row).OrderByDescending(r => r.OutSinceLocal).ToList();

        // PENDING DISPOSITION APPROVAL: originals out for approval (a paper state, not a release).
        var pending = new List<SuspenseRow>();
        foreach (var paper in papers.Where(p => p.OriginalDisposition == OriginalDisposition.SentForDispositionApproval))
        {
            var voucher = byVoucher.GetValueOrDefault(paper.VoucherId);
            var sent = paper.Events.Where(e => e.Kind == PhysicalDocumentEventKind.OriginalSentForDispositionApproval).OrderByDescending(e => e.OccurredAtUtc).FirstOrDefault();
            var since = sent?.OccurredAtUtc ?? now;
            var days = Math.Max(0, (int)Math.Floor((now - since).TotalDays));
            pending.Add(new SuspenseRow(null, paper.VoucherId, voucher?.DisplayIdentifier ?? $"voucher {paper.VoucherId}", SuspenseCategory.PendingDispositionApproval,
                "trial counsel / prosecutor (original out for disposition approval)", null, null, PaperCopyKind.Original, since, days,
                0, voucher is null ? string.Empty : string.Join(", ", voucher.CurrentFormLines.Select(i => i.ItemNumber)),
                null, null, null, false, days > threshold, labels.GetValueOrDefault(paper.CurrentContainerId ?? 0, "(folder)")));
        }

        var advisories = Advisories(releases, vouchers, papers, containers);

        return new SuspenseDashboardView(evidenceRoomId, now, threshold,
            open.Where(r => r.Category == SuspenseCategory.Usacil).ToList(),
            open.Where(r => r.Category == SuspenseCategory.Adjudication).ToList(),
            pending.OrderByDescending(r => r.DaysOut).ToList(),
            closed, advisories);
    }

    /// <summary>SUSP-017: the release records against the item states, the paper record, the custody chain and the folder counts.</summary>
    internal static IReadOnlyList<SuspenseConsistencyAdvisory> Advisories(
        IReadOnlyList<TemporaryRelease> releases, IReadOnlyList<Emc.Domain.Cases.EvidenceVoucher> vouchers, IReadOnlyList<PhysicalVoucherDocument> papers, IReadOnlyList<PhysicalFileContainer> containers)
    {
        var result = new List<SuspenseConsistencyAdvisory>();
        var openItems = releases.Where(r => r.IsOpen).SelectMany(r => r.Items.Where(i => i.Status == TemporaryReleaseItemStatus.Out).Select(i => (Release: r, Item: i))).ToList();
        var openByItem = openItems.ToDictionary(x => x.Item.EvidenceItemId, x => x.Release);
        var papersByVoucher = papers.ToDictionary(p => p.VoucherId);

        foreach (var voucher in vouchers)
        {
            foreach (var item in voucher.CurrentFormLines)
            {
                var onOpenRelease = openByItem.ContainsKey(item.Id);
                if (item.AccountabilityStatus == AccountabilityStatus.TemporarilyReleased && !onOpenRelease)
                {
                    result.Add(new("SCV-001", "2-7a, 2-7b", voucher.Id, voucher.DisplayIdentifier,
                        $"Item {item.ItemNumber} is on temporary release, but no open temporary-release record names it. Record the release (with its paper and recipient), or check the item's state."));
                }

                if (item.AccountabilityStatus != AccountabilityStatus.TemporarilyReleased && onOpenRelease)
                {
                    result.Add(new("SCV-002", "2-7b", voucher.Id, voucher.DisplayIdentifier,
                        $"Item {item.ItemNumber} is out on an open temporary release, but its state is {item.AccountabilityStatus}. Record the return on the release, or check the item's state."));
                }

                // The chain's current holder should be the recipient while the item is out.
                if (onOpenRelease && item.CurrentCustodyHolderPartyId is int holderId && holderId != openByItem[item.Id].ReceivedByPartyId)
                {
                    result.Add(new("SCV-003", "2-3f, COC-001", voucher.Id, voucher.DisplayIdentifier,
                        $"Item {item.ItemNumber} is out on a release to {openByItem[item.Id].ReceivedBy?.DisplayName ?? "(recipient)"}, but its chain of custody shows a later holder. A custody event was recorded after the release without a return; check the chain."));
                }
            }

            // Paper vs releases.
            papersByVoucher.TryGetValue(voucher.Id, out var paper);
            var openHere = releases.Where(r => r.IsOpen && r.VoucherId == voucher.Id).ToList();
            var originalOutOnRelease = openHere.Any(r => r.PaperAccompanying == PaperCopyKind.Original);
            var copiesOutOnReleases = openHere.Count(r => r.PaperAccompanying == PaperCopyKind.AdditionalTemporaryReleaseCopy);

            if (paper is not null)
            {
                if (originalOutOnRelease && paper.OriginalDisposition != OriginalDisposition.AccompanyingTemporaryRelease)
                {
                    result.Add(new("SCV-004", "2-4f(2), 2-7b", voucher.Id, voucher.DisplayIdentifier,
                        $"An open release says the ORIGINAL DA Form 4137 accompanied the evidence, but the paper record says the original is {paper.OriginalDisposition}."));
                }

                if (!originalOutOnRelease && paper.OriginalDisposition == OriginalDisposition.AccompanyingTemporaryRelease)
                {
                    result.Add(new("SCV-005", "2-4f(2), 2-7b", voucher.Id, voucher.DisplayIdentifier,
                        "The paper record says the original DA Form 4137 accompanies a temporary release, but no open release for this voucher took the original."));
                }

                if (copiesOutOnReleases != paper.AdditionalCopiesOut)
                {
                    result.Add(new("SCV-006", "2-7b", voucher.Id, voucher.DisplayIdentifier,
                        $"The paper record counts {paper.AdditionalCopiesOut} copy/copies out, but {copiesOutOnReleases} open release(s) took a copy."));
                }

                foreach (var r in openHere.Where(r => r.SuspenseFolderContainerId != paper.FirstCopyContainerId && paper.FirstCopyContainerId is not null))
                {
                    result.Add(new("SCV-007", "2-4f(3), 2-7b", voucher.Id, voucher.DisplayIdentifier,
                        $"Release {r.Id} names a suspense folder other than the one the paper record says holds the first copy."));
                }
            }
            else if (openHere.Count > 0)
            {
                result.Add(new("SCV-004", "2-4f(2), 2-7b", voucher.Id, voucher.DisplayIdentifier, "An open release exists but the voucher has no paper record at all."));
            }
        }

        // Folder kinds vs categories.
        var kinds = containers.ToDictionary(c => c.Id, c => c.Kind);
        foreach (var r in releases.Where(r => r.IsOpen && r.PaperAccompanying == PaperCopyKind.Original))
        {
            var expected = r.Category == SuspenseCategory.Usacil ? PhysicalFileKind.SuspenseUsacil : PhysicalFileKind.SuspenseAdjudication;
            if (kinds.TryGetValue(r.SuspenseFolderContainerId, out var kind) && kind != expected)
            {
                var voucher = vouchers.FirstOrDefault(v => v.Id == r.VoucherId);
                result.Add(new("SCV-008", "2-4f(3)", r.VoucherId, voucher?.DisplayIdentifier,
                    $"Release {r.Id} ({r.Category}) files its first copy in a {kind} folder; the regulation's folder for it is {expected}."));
            }
        }

        return result;
    }
}
