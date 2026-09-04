using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Items;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Emc.Domain.Suspense;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Suspense;

/// <summary>Who received the evidence (AR 195-5 2-7b, 2-7e). Exactly one shape per kind; resolved to a CustodyParty row by the server.</summary>
public sealed record ReleaseRecipient(
    CustodyPartyKind Kind,
    string Name,
    string? TitleOrGrade = null,
    string? OrganizationOrAgency = null,
    bool IdentificationPresented = false,
    string? AccountableMailNumber = null,
    string? Carrier = null);

/// <summary>
/// One temporary release, recorded as ONE unit of work (SUSP-010): the release, the custody and
/// status events on every item, the paper original leaving and the first copy filed in the
/// suspense folder, and the audit row. Either all of it is written or none of it.
/// </summary>
public sealed record TemporaryReleaseRequest(
    int VoucherId,
    IReadOnlyList<int> ItemIds,
    SuspenseCategory Category,
    ReleaseRecipient ReceivedBy,
    string Purpose,
    string? Destination,
    DateTimeOffset ReleasedAtLocal,
    int SuspenseFolderContainerId,
    bool PhysicalInventoryPerformedAttested,
    bool Original4137ReceivedBySignedAttested,
    bool FirstCopyReceivedBySignedAttested,
    bool IdentificationPresentedAttested,
    bool ObligationsInformedAttested,
    DateTimeOffset? ExpectedFollowUpLocal = null,
    int? SourceDocumentId = null,
    string? Notes = null,

    /// <summary>
    /// AR 195-5 2-7b (SUSP-008). The ORIGINAL accompanies the evidence unless copies are in use:
    /// when the original is already out with another recipient, or several recipients are served
    /// at once, a COPY goes with each release and the first copy carries every chain.
    /// </summary>
    PaperCopyKind PaperAccompanying = PaperCopyKind.Original,

    /// <summary>Required for a USACIL-category release (2-7c).</summary>
    LaboratoryDetails? Laboratory = null);

/// <summary>AR 195-5 2-7c/2-7e/2-7f: which laboratory, whether a non-USACIL laboratory was coordinated with the USACIL, the DD Form 2922 reference, the shipping document.</summary>
public sealed record LaboratoryDetails(string LaboratoryName, bool CoordinatedWithUsacilAttested = false, string? ExaminationRequestReference = null, string? ShippingDocumentReference = null);

/// <summary>One recipient's share of a multi-recipient release (AR 195-5 2-7b, SUSP-008). A copy accompanies each.</summary>
public sealed record RecipientReleasePart(
    IReadOnlyList<int> ItemIds,
    SuspenseCategory Category,
    ReleaseRecipient ReceivedBy,
    string Purpose,
    string? Destination,
    bool PhysicalInventoryPerformedAttested,
    bool Original4137ReceivedBySignedAttested,
    bool FirstCopyReceivedBySignedAttested,
    bool IdentificationPresentedAttested,
    bool ObligationsInformedAttested,
    DateTimeOffset? ExpectedFollowUpLocal = null,
    string? Notes = null,
    LaboratoryDetails? Laboratory = null);

/// <summary>
/// AR 195-5 2-7d: a controlled substance returned after a temporary release other than for
/// laboratory examination shows an apparent change: it is annotated in the Purpose of Change of
/// Custody column and an MFR explaining it is prepared and attached to the DA Form 4137.
/// </summary>
public sealed record ControlledSubstanceApparentChange(string Annotation, string MfrReference);

/// <summary>
/// One item coming back. No location is assigned by default (LOC-008): the custodian either
/// names the bin it goes to now, or explicitly confirms it goes back to the bin it was in.
/// </summary>
public sealed record ReturnedItem(int ItemId, int? StorageLocationId = null, bool ConfirmReturnToPriorLocation = false, ControlledSubstanceApparentChange? ApparentChange = null);

/// <summary>
/// AR 195-5 2-7b: evidence comes back. The custody event per item (returner -> custodian), the
/// status back to the evidence room, the location only as the custodian says, the release's
/// items marked returned, and - when nothing is left out - the paper: the original to the
/// active file and the first copy filed with it, or the returned copy's chain onto the first
/// copy. One unit of work. <paramref name="ReturnedBy"/> defaults to the release's recipient;
/// for a laboratory return by mail the accountable mail number stands in Released By (2-7e).
/// </summary>
public sealed record ReturnFromTemporaryReleaseRequest(
    int TemporaryReleaseId,
    IReadOnlyList<ReturnedItem> Items,
    DateTimeOffset ReturnedAtLocal,
    bool OriginalAnnotatedByCustodianAndReturnerAttested,
    bool FirstCopyChainAnnotatedAttested,
    ReleaseRecipient? ReturnedBy = null,
    int? ActiveFileContainerId = null,
    int? SourceDocumentId = null,
    string? Notes = null);

/// <summary>
/// An item on a release that will not come back: entered in the record of trial (final
/// disposition, 3-1a(4), 2-8e(4)) or consumed / retained by the laboratory (2-7c(2), with an
/// MFR). The item moves to DispositionPending; the disposition itself is the 2-8/2-9 workflow.
/// </summary>
public sealed record NotReturnedRequest(
    int TemporaryReleaseId,
    IReadOnlyList<int> ItemIds,
    NotReturnedReason Reason,
    DateTimeOffset OccurredAtLocal,
    string Narrative,
    string? MfrReference = null);

/// <summary>
/// Items on one DA Form 4137 released to more than one agency or person at the same time
/// (AR 195-5 2-7b, SUSP-008): copies are used; the original stays in the active file, noted;
/// the first copy goes to the suspense folder named and carries the chain of custody for all
/// the evidence; each part becomes its own TemporaryRelease. One unit of work.
/// </summary>
public sealed record MultiRecipientReleaseRequest(
    int VoucherId,
    DateTimeOffset ReleasedAtLocal,
    int FirstCopySuspenseFolderContainerId,
    IReadOnlyList<RecipientReleasePart> Parts,
    int? SourceDocumentId = null,
    string? Notes = null);

public sealed record RecordSuspenseContactRequest(
    int TemporaryReleaseId,
    DateTimeOffset ContactedAtLocal,
    ContactMethod Method,
    string ContactedPerson,
    ContactOutcome Outcome,
    string? Narrative,
    DateTimeOffset? NextFollowUpLocal = null);

public sealed record TemporaryReleaseItemRow(int EvidenceItemId, int ItemNumber, string DescriptionForForm, TemporaryReleaseItemStatus Status, int ReleaseCustodyEventId, int? ReturnCustodyEventId, DateTimeOffset? ReturnedAtUtc);
public sealed record TemporaryReleaseEventRow(TemporaryReleaseEventKind Kind, DateTimeOffset OccurredAtUtc, DateTimeOffset RecordedAtUtc, string RecordedByName, int? ItemNumber, string? Narrative);
public sealed record SuspenseContactRow(DateTimeOffset ContactedAtLocal, DateTimeOffset RecordedAtUtc, string RecordedByName, ContactMethod Method, string ContactedPerson, ContactOutcome Outcome, string? Narrative, DateTimeOffset? NextFollowUpLocal);

public sealed record TemporaryReleaseView(
    int Id,
    int VoucherId,
    string VoucherIdentifier,
    int EvidenceRoomId,
    SuspenseCategory Category,
    TemporaryReleaseStatus Status,
    PaperCopyKind PaperAccompanying,
    string ReleasedByDisplayName,
    string ReceivedByDisplayName,
    CustodyPartyKind ReceivedByKind,
    string? ReceivedByOrganization,
    string Purpose,
    string? Destination,
    DateTimeOffset ReleasedAtLocal,
    DateTimeOffset RecordedAtUtc,
    string RecordedByName,
    DateTimeOffset? ExpectedFollowUpLocal,
    string SuspenseFolderLabel,
    bool PhysicalInventoryPerformedAttested,
    bool Original4137ReceivedBySignedAttested,
    bool FirstCopyReceivedBySignedAttested,
    bool IdentificationPresentedAttested,
    bool ObligationsInformedAttested,
    int DaysOut,
    int ItemsOut,
    DateTimeOffset? LastContactAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? Notes,
    IReadOnlyList<TemporaryReleaseItemRow> Items,
    IReadOnlyList<TemporaryReleaseEventRow> Events,
    IReadOnlyList<SuspenseContactRow> Contacts,
    string? LaboratoryName = null,
    bool LaboratoryCoordinatedWithUsacilAttested = false,
    string? ExaminationRequestReference = null,
    string? ShippingDocumentReference = null,
    bool OriginalAnnotatedOnReturnAttested = false,
    bool FirstCopyChainAnnotatedOnReturnAttested = false);

public interface ITemporaryReleaseService
{
    /// <summary>Records a temporary release atomically. Returns the release id.</summary>
    Task<OperationResult<int>> ReleaseAsync(TemporaryReleaseRequest request, CancellationToken ct = default);

    /// <summary>Records releases to several recipients at once, with copies (2-7b, SUSP-008), atomically. Returns the release ids in part order.</summary>
    Task<OperationResult<IReadOnlyList<int>>> ReleaseToMultipleAsync(MultiRecipientReleaseRequest request, CancellationToken ct = default);

    /// <summary>AR 195-5 2-7a: a contact with the holder. Append-only.</summary>
    Task<OperationResult> RecordContactAsync(RecordSuspenseContactRequest request, CancellationToken ct = default);

    /// <summary>AR 195-5 2-7b: items come back. One unit of work.</summary>
    Task<OperationResult> ReturnAsync(ReturnFromTemporaryReleaseRequest request, CancellationToken ct = default);

    /// <summary>An item accounted for without returning (record of trial; consumed or retained by the laboratory).</summary>
    Task<OperationResult> RecordNotReturnedAsync(NotReturnedRequest request, CancellationToken ct = default);

    Task<TemporaryReleaseView?> GetAsync(int releaseId, CancellationToken ct = default);
    Task<IReadOnlyList<TemporaryReleaseView>> GetForVoucherAsync(int voucherId, CancellationToken ct = default);
}

/// <summary>
/// Temporary release of evidence (AR 195-5 2-7a, 2-7b, 2-7e; 2-4f(2), 2-4f(3)) and the suspense
/// contact history. Every write is the custodian's act (1-4h; the ReleaseTemporarily permission
/// needs an active appointment) and every write is one SaveChanges - one SQL transaction.
///
/// What a release writes, together:
///   - a CustodyParty for the recipient (external person, organization, or accountable mail
///     number - 2-7e), and one for the releasing custodian;
///   - per item: a CustodyEvent (custodian -> recipient; SCRCNI when the item is sealed; the
///     Purpose of Change of Custody column; OccurredAt = when it left, RecordedAt = now) and a
///     StatusEvent InEvidenceRoom -> TemporarilyReleased, both sealed into the item's chain;
///   - the TemporaryRelease with its items (each tied to its custody event), its 2-7b paper
///     attestations, and its Released event;
///   - the paper: the ORIGINAL leaves the active binder with the evidence and the FIRST COPY is
///     filed in the suspense folder the request names (2-4f(2), 2-4f(3), 2-7b), through the same
///     PhysicalVoucherDocument every other paper action uses;
///   - an audit row with identifiers only.
/// </summary>
public sealed class TemporaryReleaseService : ITemporaryReleaseService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IItemEventRecorder _events;
    private readonly IClock _clock;

    public TemporaryReleaseService(IEmcDbContext db, IEvidenceAuthorizationService authorization, ICurrentUser currentUser, IAuditRecorder audit, IItemEventRecorder events, IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _events = events;
        _clock = clock;
    }

    public async Task<OperationResult<int>> ReleaseAsync(TemporaryReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ReceivedBy);

        var context = await BeginAsync(request.VoucherId, request.SourceDocumentId, ct);
        if (context.Failure is not null)
        {
            return OperationResult<int>.Failure(context.Failure.Error!, context.Failure.RequirementId);
        }

        var part = new RecipientReleasePart(request.ItemIds, request.Category, request.ReceivedBy, request.Purpose, request.Destination,
            request.PhysicalInventoryPerformedAttested, request.Original4137ReceivedBySignedAttested, request.FirstCopyReceivedBySignedAttested,
            request.IdentificationPresentedAttested, request.ObligationsInformedAttested, request.ExpectedFollowUpLocal, request.Notes, request.Laboratory);

        var staged = await StageAsync(context, part, request.PaperAccompanying, request.SuspenseFolderContainerId, request.ReleasedAtLocal, request.SourceDocumentId, ct);
        if (staged.Failure is not null)
        {
            return OperationResult<int>.Failure(staged.Failure.Error!, staged.Failure.RequirementId);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded, nameof(TemporaryRelease), context.Voucher.DisplayIdentifier,
            newValue: $"{request.Category}; {staged.Items.Count} item(s); recipient kind {staged.Release.ReceivedBy.Kind}; paper {request.PaperAccompanying}; suspense folder {staged.Release.SuspenseFolderContainerId}",
            reason: "AR 195-5 2-7a/2-7b temporary release");

        var saved = await CommitAsync(ct);
        if (saved is not null)
        {
            return OperationResult<int>.Failure(saved.Error!, saved.RequirementId);
        }

        return OperationResult<int>.Success(staged.Release.Id, [.. Warnings(staged.Release)]);
    }

    public async Task<OperationResult<IReadOnlyList<int>>> ReleaseToMultipleAsync(MultiRecipientReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parts is null || request.Parts.Count < 2)
        {
            return OperationResult<IReadOnlyList<int>>.Failure("AR 195-5 para 2-7b: a multi-recipient release names at least two recipients; a single recipient takes the original.", "SUSP-008");
        }

        var allItems = request.Parts.SelectMany(p => p.ItemIds ?? []).ToList();
        if (allItems.Count != allItems.Distinct().Count())
        {
            return OperationResult<IReadOnlyList<int>>.Failure("An item is named for more than one recipient.", "SUSP-008");
        }

        var context = await BeginAsync(request.VoucherId, request.SourceDocumentId, ct);
        if (context.Failure is not null)
        {
            return OperationResult<IReadOnlyList<int>>.Failure(context.Failure.Error!, context.Failure.RequirementId);
        }

        // Copies for every recipient: the original stays in the active file (2-7b, FIL-015).
        if (context.Paper!.OriginalDisposition != OriginalDisposition.HeldActive)
        {
            return OperationResult<IReadOnlyList<int>>.Failure(
                "AR 195-5 para 2-7b: releasing to several recipients at once uses copies while the original stays in the active file. The original is not in this room's active file.", "FIL-015");
        }

        var releases = new List<TemporaryRelease>();
        foreach (var part in request.Parts)
        {
            var staged = await StageAsync(context, part, PaperCopyKind.AdditionalTemporaryReleaseCopy, request.FirstCopySuspenseFolderContainerId, request.ReleasedAtLocal, request.SourceDocumentId, ct);
            if (staged.Failure is not null)
            {
                return OperationResult<IReadOnlyList<int>>.Failure(staged.Failure.Error!, staged.Failure.RequirementId);
            }

            releases.Add(staged.Release);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded, nameof(TemporaryRelease), context.Voucher.DisplayIdentifier,
            newValue: $"{request.Parts.Count} recipients at once with copies (2-7b); {allItems.Count} item(s); first copy in folder {request.FirstCopySuspenseFolderContainerId}",
            reason: "AR 195-5 2-7b multi-recipient temporary release (SUSP-008)");

        var saved = await CommitAsync(ct);
        if (saved is not null)
        {
            return OperationResult<IReadOnlyList<int>>.Failure(saved.Error!, saved.RequirementId);
        }

        var warnings = releases.SelectMany(Warnings).Distinct().ToList();
        warnings.Insert(0, "AR 195-5 2-7b: copies accompanied the evidence; the note that copies were made is recorded on the original and the first copy; the chain of custody for all the evidence is recorded on the first copy in the suspense folder.");
        return OperationResult<IReadOnlyList<int>>.Success(releases.Select(r => r.Id).ToList(), [.. warnings]);
    }

    /// <summary>What every release path needs: the voucher (tracked), the authorization, the paper record, the custodian's party.</summary>
    private sealed class ReleaseContext
    {
        public EvidenceVoucher Voucher { get; init; } = null!;
        public PhysicalVoucherDocument? Paper { get; init; }
        public CustodyParty ReleasedBy { get; init; } = null!;
        public OperationResult? Failure { get; init; }
        public HashSet<int> StagedItemIds { get; } = [];
    }

    private sealed record Staged(TemporaryRelease Release, IReadOnlyList<EvidenceItem> Items, OperationResult? Failure);

    private async Task<ReleaseContext> BeginAsync(int voucherId, int? sourceDocumentId, CancellationToken ct)
    {
        var voucher = await _db.EvidenceVouchers
            .Include(v => v.Items)
            .Include(v => v.DocumentNumberAssignments)
            .FirstOrDefaultAsync(v => v.Id == voucherId, ct);
        if (voucher is null)
        {
            return new ReleaseContext { Failure = OperationResult.Failure("Voucher not found.", "VCH-001") };
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ReleaseTemporarily, voucher.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(AuditEventType.PermissionDenied, nameof(TemporaryRelease), voucher.DisplayIdentifier, reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return new ReleaseContext { Voucher = voucher, Failure = OperationResult.Failure(decision.Reason!, decision.RequirementId) };
        }

        if (!voucher.HasOfficialDocumentNumber)
        {
            return new ReleaseContext { Voucher = voucher, Failure = OperationResult.Failure(
                "AR 195-5 para 2-7c(3): evidence is released to the evidence custodian for accountability before it goes anywhere. This voucher has not been received and numbered.", "SUSP-001") };
        }

        if (sourceDocumentId is int docId)
        {
            var docRoom = await _db.SourceDocuments.AsNoTracking().Where(d => d.Id == docId).Select(d => (int?)d.EvidenceRoomId).FirstOrDefaultAsync(ct);
            if (docRoom is null || docRoom != voucher.EvidenceRoomId)
            {
                return new ReleaseContext { Voucher = voucher, Failure = OperationResult.Failure("The source document named is not in this evidence room.", "DOC-001") };
            }
        }

        var paper = await _db.PhysicalVoucherDocuments.Include(d => d.Events).FirstOrDefaultAsync(d => d.VoucherId == voucher.Id, ct);
        if (paper is null || paper.OriginalDisposition == OriginalDisposition.NotYetFiled)
        {
            return new ReleaseContext { Voucher = voucher, Failure = OperationResult.Failure(
                "AR 195-5 paras 2-4f(2) and 2-7b: the ORIGINAL DA Form 4137 (or, when copies are in use, a copy) accompanies temporarily released evidence and the first copy goes in the suspense folder. "
                + "This room's paper record does not show the original filed. Record the paper filing first.", "FIL-005") };
        }

        var custodianUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, ct);
        if (custodianUser is null)
        {
            return new ReleaseContext { Voucher = voucher, Failure = OperationResult.Failure("The signed-in custodian has no user record.", "IAM-001") };
        }

        var releasedBy = CustodyParty.ForUser(custodianUser);
        _db.CustodyParties.Add(releasedBy);
        return new ReleaseContext { Voucher = voucher, Paper = paper, ReleasedBy = releasedBy };
    }

    /// <summary>
    /// Validates one recipient's release and stages every row it needs on the tracker. Nothing
    /// is saved here; the caller commits once. Refusals leave the tracker with earlier staged
    /// work that the caller then discards by not saving.
    /// </summary>
    private async Task<Staged> StageAsync(ReleaseContext context, RecipientReleasePart part, PaperCopyKind paperAccompanying, int suspenseFolderContainerId,
        DateTimeOffset releasedAtLocal, int? sourceDocumentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part.ReceivedBy);
        var voucher = context.Voucher;
        var paper = context.Paper!;

        static Staged Fail(string message, string requirementId) => new(null!, [], OperationResult.Failure(message, requirementId));

        // The items: on this voucher, on the current form, in the evidence room, not already
        // staged for another recipient in this unit of work.
        if (part.ItemIds is null || part.ItemIds.Count == 0)
        {
            return Fail("Name at least one item to release.", "SUSP-001");
        }

        var items = new List<EvidenceItem>();
        foreach (var itemId in part.ItemIds.Distinct())
        {
            var item = voucher.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is null || item.IsWithdrawnFromForm)
            {
                return Fail("An item named is not on this voucher's current form.", "SUSP-001");
            }

            if (!context.StagedItemIds.Add(itemId))
            {
                return Fail("An item is named for more than one recipient.", "SUSP-008");
            }

            if (item.AccountabilityStatus != AccountabilityStatus.InEvidenceRoom)
            {
                var why = item.AccountabilityStatus switch
                {
                    AccountabilityStatus.TemporarilyReleased => "it is already on temporary release; record its return first",
                    AccountabilityStatus.LongTermRetention => "it is sealed in a long-term retention container (2-13); record its removal to the evidence room first",
                    AccountabilityStatus.DispositionPending => "disposition approval is pending for it (2-8)",
                    AccountabilityStatus.DiscrepancyReview or AccountabilityStatus.Inquiry => "it cannot be located (3-3)",
                    _ when AccountabilityStateMachine.IsBeforeCustodianReceipt(item.AccountabilityStatus) => "the custodian has not received it (2-4c)",
                    _ => $"it is {item.AccountabilityStatus}"
                };
                return Fail($"AR 195-5 para 2-7a: item {item.ItemNumber} cannot be temporarily released: {why}.", "SUSP-001");
            }

            items.Add(item);
        }

        var itemIds = items.Select(i => i.Id).ToList();
        var alreadyOut = await _db.TemporaryReleaseItems.AsNoTracking()
            .AnyAsync(t => itemIds.Contains(t.EvidenceItemId) && t.Status == TemporaryReleaseItemStatus.Out, ct);
        if (alreadyOut)
        {
            return Fail("An item named is out on an open temporary release.", "SUSP-001");
        }

        // The suspense folder: this room's; for the original path, of the category's kind (2-4f(3));
        // for the copy path, the folder holding the first copy (or, when none is filed yet, the one
        // to file it in - of the category's kind).
        var folder = await _db.PhysicalFileContainers.FirstOrDefaultAsync(c => c.Id == suspenseFolderContainerId, ct);
        if (folder is null || folder.EvidenceRoomId != voucher.EvidenceRoomId)
        {
            return Fail("Suspense folder not found in this evidence room.", "FIL-001");
        }

        var expectedKind = part.Category switch
        {
            SuspenseCategory.Usacil => PhysicalFileKind.SuspenseUsacil,
            SuspenseCategory.Adjudication => PhysicalFileKind.SuspenseAdjudication,
            _ => PhysicalFileKind.SuspensePendingDispositionApproval
        };

        PhysicalFileContainer? home = null;
        if (paperAccompanying == PaperCopyKind.Original)
        {
            if (folder.Kind != expectedKind)
            {
                return Fail($"AR 195-5 para 2-4f(3): a {part.Category} release files its first copy in the {expectedKind} folder; \"{folder.Label}\" is a {folder.Kind} folder.", "FIL-005");
            }

            if (paper.OriginalDisposition != OriginalDisposition.HeldActive || paper.HomeActiveContainerId is null)
            {
                return Fail(
                    "AR 195-5 paras 2-4f(2) and 2-7b: the ORIGINAL DA Form 4137 accompanies temporarily released evidence. This room's paper record shows the original "
                    + $"is {paper.OriginalDisposition}. If it is already out with another recipient, release a COPY with this evidence (2-7b, SUSP-008).", "FIL-005");
            }

            if (paper.AdditionalCopiesOut > 0 || paper.FirstCopyContainerId is not null)
            {
                return Fail("AR 195-5 para 2-7b: copies of this form are in use, so the original stays in the active file. Release a COPY with this evidence (SUSP-008).", "FIL-015");
            }

            home = await _db.PhysicalFileContainers.FirstOrDefaultAsync(c => c.Id == paper.HomeActiveContainerId, ct);
            if (home is null)
            {
                return Fail("The active file holding the original was not found.", "FIL-001");
            }
        }
        else
        {
            if (paper.OriginalDisposition is not (OriginalDisposition.HeldActive or OriginalDisposition.AccompanyingTemporaryRelease))
            {
                return Fail($"AR 195-5 para 2-7b: a copy accompanies evidence while the original is in the active file or out with another recipient. The original is {paper.OriginalDisposition}.", "FIL-015");
            }

            if (paper.FirstCopyContainerId is int firstCopyId)
            {
                if (folder.Id != firstCopyId)
                {
                    var holder = await _db.PhysicalFileContainers.AsNoTracking().Where(c => c.Id == firstCopyId).Select(c => c.Label).FirstOrDefaultAsync(ct);
                    return Fail($"AR 195-5 para 2-7b: the chain of custody for all the evidence is recorded on the first copy, which is in \"{holder}\". Name that folder for this release.", "FIL-015");
                }
            }
            else if (folder.Kind != expectedKind)
            {
                return Fail($"AR 195-5 para 2-4f(3): the first copy goes in the {expectedKind} folder for a {part.Category} release; \"{folder.Label}\" is a {folder.Kind} folder.", "FIL-005");
            }
        }

        CustodyParty recipient;
        try
        {
            recipient = ToParty(part.ReceivedBy);
        }
        catch (DomainRuleViolationException ex)
        {
            return Fail(ex.Message, ex.RequirementId);
        }

        var now = _clock.UtcNow;
        var attestations = new PaperReleaseAttestations(
            part.PhysicalInventoryPerformedAttested, part.Original4137ReceivedBySignedAttested, part.FirstCopyReceivedBySignedAttested,
            part.IdentificationPresentedAttested, part.ObligationsInformedAttested);

        var laboratory = part.Laboratory is null ? null
            : new LaboratorySubmission(part.Laboratory.LaboratoryName, part.Laboratory.CoordinatedWithUsacilAttested, part.Laboratory.ExaminationRequestReference, part.Laboratory.ShippingDocumentReference);

        TemporaryRelease release;
        try
        {
            release = TemporaryRelease.Create(
                voucher.Id, voucher.EvidenceRoomId, part.Category, context.ReleasedBy, recipient, part.Purpose, part.Destination,
                releasedAtLocal, now, _currentUser.UserId, part.ExpectedFollowUpLocal, attestations, folder.Id, paperAccompanying, laboratory, part.Notes);

            _db.CustodyParties.Add(recipient);
            _db.TemporaryReleases.Add(release);

            var agency = recipient.OrganizationOrAgency ?? (recipient.Kind == CustodyPartyKind.Organization ? recipient.DisplayName : null);
            var paperNote = paperAccompanying == PaperCopyKind.Original ? "original DA Form 4137 accompanies" : "copy of DA Form 4137 accompanies (2-7b)";
            foreach (var item in items.OrderBy(i => i.ItemNumber))
            {
                // COC-003. OccurredAt is when the evidence left; RecordedAt is now. The chain
                // carries the release's purpose; the item's own sealed state decides SCRCNI (2-3f).
                var custody = new CustodyEvent(
                    releasedBy: context.ReleasedBy,
                    receivedBy: recipient,
                    purposeOfChangeOfCustody: part.Purpose,
                    occurredAtLocal: releasedAtLocal,
                    recordedAtUtc: now,
                    recordedByUserId: _currentUser.UserId,
                    isScrcni: item.IsSealed,
                    destination: part.Destination,
                    agency: agency,
                    notes: $"Temporary release ({part.Category}); {paperNote}.");
                if (sourceDocumentId is int sourceDocId)
                {
                    custody.AttachSourceDocument(sourceDocId);
                }

                await _events.AppendAsync(item, custody, ct);

                var from = item.AccountabilityStatus;
                item.TransitionTo(AccountabilityStatus.TemporarilyReleased);
                await _events.AppendAsync(item, new StatusEvent(
                    fromStatus: from,
                    toStatus: AccountabilityStatus.TemporarilyReleased,
                    reason: $"Temporarily released to {recipient.DisplayName} - {part.Purpose} (AR 195-5 2-7a, 2-7b).",
                    occurredAtLocal: releasedAtLocal,
                    recordedAtUtc: now,
                    recordedByUserId: _currentUser.UserId), ct);

                // AR 195-5 2-7c: a laboratory submission is on the item's own history too.
                if (laboratory is not null)
                {
                    await _events.AppendAsync(item, new ExaminationEvent(
                        laboratory: laboratory.LaboratoryName,
                        occurredAtLocal: releasedAtLocal,
                        recordedAtUtc: now,
                        recordedByUserId: _currentUser.UserId,
                        examinationRequestReference: laboratory.ExaminationRequestReference,
                        notes: laboratory.IsUsacil ? null : "AR 195-5 2-7c(1): laboratory other than the USACIL, after prior coordination with the USACIL."), ct);
                }

                release.AddItem(item.Id, item.ItemNumber, custody);
            }

            release.MarkReleased(_currentUser.UserId, now, part.Notes);

            // The paper, in the same unit of work (2-4f(2), 2-7b).
            if (paperAccompanying == PaperCopyKind.Original)
            {
                paper.ReleaseOriginalWithEvidence(home!, folder, _currentUser.UserId, releasedAtLocal,
                    $"Original released with the evidence to {recipient.DisplayName}; first copy filed in {folder.Label}.");
            }
            else
            {
                paper.ReleaseCopyWithEvidence(folder, _currentUser.UserId, releasedAtLocal,
                    $"Copy released with the evidence to {recipient.DisplayName} (2-7b); chain recorded on the first copy in {folder.Label}.");
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return Fail(ex.Message, ex.RequirementId);
        }

        return new Staged(release, items, null);
    }

    private async Task<OperationResult?> CommitAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException)
        {
            // The one-open-per-item index or a concurrency stamp: somebody else released, filed
            // or changed one of these rows first. Nothing was written.
            return OperationResult.Failure("Another change to this voucher, its items or its paper record happened first. Reload and try again.", "SEC-007");
        }
    }

    private static IEnumerable<string> Warnings(TemporaryRelease release)
    {
        yield return "The custodian maintains reasonable and adequate contact with the recipient until the evidence is returned (AR 195-5 2-7a). Record each contact on the release. The regulation sets no day limit; any threshold shown is a local management threshold.";
        if (release.ReceivedBy.Kind == CustodyPartyKind.AccountableMailNumber)
        {
            yield return "AR 195-5 2-7e: the accountable mail number was entered in the Received By block; on receipt the USACIL records it in the Released By block. Confirm the laboratory's acknowledgement of receipt.";
        }

        if (release.PaperAccompanying == PaperCopyKind.AdditionalTemporaryReleaseCopy)
        {
            yield return "AR 195-5 2-7b: a COPY accompanied this evidence. Note on the original and the first copy that copies have been made; record this release's chain of custody on the first copy.";
        }

        if (release.Laboratory is { IsDft: true })
        {
            yield return "AR 195-5 2-7c(2): specimens submitted to the DFT are in most instances not returned. Coordinate with the DFT to confirm whether they will be; if not, an MFR explaining the circumstances is prepared and attached to the DA Form 4137, and the items are accounted for on this release without return.";
        }

        if (release.Laboratory?.ShippingDocumentReference is not null)
        {
            yield return "AR 195-5 2-7f: a copy of the shipping document stays attached to the suspense copy of the DA Form 4137 until the addressee acknowledges receipt or the evidence is returned.";
        }
    }

    public async Task<OperationResult> ReturnAsync(ReturnFromTemporaryReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items is null || request.Items.Count == 0)
        {
            return OperationResult.Failure("Name at least one item that came back.", "SUSP-012");
        }

        var release = await _db.TemporaryReleases
            .Include(r => r.ReleasedBy).Include(r => r.ReceivedBy)
            .Include(r => r.Items).Include(r => r.Events).Include(r => r.Contacts)
            .FirstOrDefaultAsync(r => r.Id == request.TemporaryReleaseId, ct);
        if (release is null)
        {
            return OperationResult.Failure("Temporary release not found.", "SUSP-012");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ReturnFromTemporaryRelease, release.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(AuditEventType.PermissionDenied, nameof(TemporaryRelease), release.Id.ToString(), reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return OperationResult.Failure(decision.Reason!, decision.RequirementId);
        }

        if (!release.IsOpen)
        {
            return OperationResult.Failure("This temporary release is closed.", "SUSP-012");
        }

        var voucher = await _db.EvidenceVouchers.Include(v => v.Items).ThenInclude(i => i.Events).Include(v => v.DocumentNumberAssignments)
            .FirstAsync(v => v.Id == release.VoucherId, ct);

        // The items: on this release, still out, each named once.
        var itemIds = request.Items.Select(i => i.ItemId).ToList();
        if (itemIds.Count != itemIds.Distinct().Count())
        {
            return OperationResult.Failure("An item is named twice.", "SUSP-012");
        }

        var returning = new List<(ReturnedItem Request, EvidenceItem Item)>();
        foreach (var returned in request.Items)
        {
            var member = release.Items.FirstOrDefault(m => m.EvidenceItemId == returned.ItemId);
            if (member is null || member.Status != TemporaryReleaseItemStatus.Out)
            {
                return OperationResult.Failure($"Item {returned.ItemId} is not out on this release.", "SUSP-012");
            }

            var item = voucher.Items.First(i => i.Id == returned.ItemId);
            if (item.AccountabilityStatus != AccountabilityStatus.TemporarilyReleased)
            {
                return OperationResult.Failure($"Item {item.ItemNumber} is {item.AccountabilityStatus}, not on temporary release.", "SUSP-012");
            }

            if (returned.StorageLocationId is not null && returned.ConfirmReturnToPriorLocation)
            {
                return OperationResult.Failure($"Item {item.ItemNumber}: name a location OR confirm the prior one, not both.", "LOC-008");
            }

            // AR 195-5 2-7d: only after a release OTHER than for laboratory examination.
            if (returned.ApparentChange is not null)
            {
                if (release.Category == SuspenseCategory.Usacil)
                {
                    return OperationResult.Failure(
                        $"Item {item.ItemNumber}: AR 195-5 para 2-7d applies to controlled substances returned after a temporary release other than for laboratory examination. Consumption or change in examination is documented by the laboratory report, not by a 2-7d annotation.", "SUSP-015");
                }

                if (string.IsNullOrWhiteSpace(returned.ApparentChange.Annotation) || string.IsNullOrWhiteSpace(returned.ApparentChange.MfrReference))
                {
                    return OperationResult.Failure(
                        $"Item {item.ItemNumber}: AR 195-5 para 2-7d: an apparent change in a controlled substance is annotated in the Purpose of Change of Custody column AND an MFR explaining it is prepared and attached to the DA Form 4137. Record the annotation and the MFR reference.", "SUSP-015");
                }
            }

            returning.Add((returned, item));
        }

        // Where each item goes now - only as the custodian says (LOC-008).
        var locations = new Dictionary<int, Emc.Domain.Storage.StorageLocation>();
        foreach (var (returned, item) in returning)
        {
            int? locationId = returned.StorageLocationId ?? (returned.ConfirmReturnToPriorLocation ? item.CurrentLocationId : null);
            if (returned.ConfirmReturnToPriorLocation && locationId is null)
            {
                return OperationResult.Failure($"Item {item.ItemNumber} had no recorded location before it left; name the location it goes to.", "LOC-008");
            }

            if (locationId is int lid)
            {
                var location = await _db.StorageLocations.Include(l => l.Parent).FirstOrDefaultAsync(l => l.Id == lid, ct);
                if (location is null || location.EvidenceRoomId != release.EvidenceRoomId)
                {
                    return OperationResult.Failure($"Item {item.ItemNumber}: the storage location is not in this evidence room.", "LOC-004");
                }

                if (!location.IsActive)
                {
                    return OperationResult.Failure($"Item {item.ItemNumber}: that storage location is no longer in use{(returned.ConfirmReturnToPriorLocation ? " (it was the prior location)" : string.Empty)}; name another.", "LOC-004");
                }

                locations[item.Id] = location;
            }
        }

        if (request.SourceDocumentId is int docId)
        {
            var docRoom = await _db.SourceDocuments.AsNoTracking().Where(d => d.Id == docId).Select(d => (int?)d.EvidenceRoomId).FirstOrDefaultAsync(ct);
            if (docRoom is null || docRoom != release.EvidenceRoomId)
            {
                return OperationResult.Failure("The source document named is not in this evidence room.", "DOC-001");
            }
        }

        var custodianUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, ct);
        if (custodianUser is null)
        {
            return OperationResult.Failure("The signed-in custodian has no user record.", "IAM-001");
        }

        // Who released it back: the recipient, unless the custodian says otherwise (2-7e: the
        // laboratory's accountable mail number in Released By, for a laboratory return only).
        CustodyParty returnedBy;
        try
        {
            if (request.ReturnedBy is null)
            {
                returnedBy = release.ReceivedBy;
            }
            else
            {
                returnedBy = ToParty(request.ReturnedBy);
                if (returnedBy.Kind == CustodyPartyKind.AccountableMailNumber && release.Category != SuspenseCategory.Usacil)
                {
                    return OperationResult.Failure("AR 195-5 para 2-7e: an accountable mail number stands in the Released By block for evidence returned from the USACIL. A person returns evidence from a legal proceeding.", "COC-006");
                }

                _db.CustodyParties.Add(returnedBy);
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        var custodian = CustodyParty.ForUser(custodianUser);
        _db.CustodyParties.Add(custodian);
        var now = _clock.UtcNow;
        var warnings = new List<string>();

        try
        {
            foreach (var (returned, item) in returning)
            {
                var purpose = "Returned from temporary release to the evidence custodian";
                string? notes = $"Temporary release {release.Id} ({release.Category}); {(release.PaperAccompanying == PaperCopyKind.Original ? "original" : "copy")} DA Form 4137 returned.";
                if (returned.ApparentChange is not null)
                {
                    purpose += $"; apparent change in controlled substance: {returned.ApparentChange.Annotation.Trim()}";
                    notes += $" AR 195-5 2-7d MFR: {returned.ApparentChange.MfrReference.Trim()}.";
                }

                var custody = new CustodyEvent(
                    releasedBy: returnedBy,
                    receivedBy: custodian,
                    purposeOfChangeOfCustody: purpose,
                    occurredAtLocal: request.ReturnedAtLocal,
                    recordedAtUtc: now,
                    recordedByUserId: _currentUser.UserId,
                    isScrcni: item.IsSealed,
                    destination: null,
                    agency: release.ReceivedBy.OrganizationOrAgency,
                    notes: notes);
                if (request.SourceDocumentId is int sourceDocId)
                {
                    custody.AttachSourceDocument(sourceDocId);
                }

                await _events.AppendAsync(item, custody, ct);

                var from = item.AccountabilityStatus;
                item.TransitionTo(AccountabilityStatus.InEvidenceRoom);
                await _events.AppendAsync(item, new StatusEvent(from, AccountabilityStatus.InEvidenceRoom,
                    $"Returned from temporary release by {returnedBy.DisplayName} (AR 195-5 2-7b).", request.ReturnedAtLocal, now, _currentUser.UserId), ct);

                if (locations.TryGetValue(item.Id, out var location))
                {
                    await _events.AppendAsync(item, new LocationEvent(location.Id, location.FullPath, request.ReturnedAtLocal, now, _currentUser.UserId,
                        returned.ConfirmReturnToPriorLocation ? "Returned to its prior location (confirmed by the custodian)" : "Placed on return from temporary release"), ct);
                }
                else
                {
                    warnings.Add($"Item {item.ItemNumber} is back in the evidence room with no location recorded. Record where it was placed (AR 195-5 2-4e).");
                }

                release.RecordItemReturned(item.Id, custody, request.ReturnedAtLocal, now, _currentUser.UserId, returned.ApparentChange is null ? null : $"2-7d apparent change annotated; MFR {returned.ApparentChange.MfrReference.Trim()}.");
            }

            // The paper, when the release is done (2-7b).
            if (!release.IsOpen)
            {
                release.RecordPaperReturned(request.OriginalAnnotatedByCustodianAndReturnerAttested, request.FirstCopyChainAnnotatedAttested, now, _currentUser.UserId);

                var paper = await _db.PhysicalVoucherDocuments.Include(d => d.Events).FirstAsync(d => d.VoucherId == release.VoucherId, ct);
                var suspense = await _db.PhysicalFileContainers.FirstAsync(c => c.Id == release.SuspenseFolderContainerId, ct);
                if (release.PaperAccompanying == PaperCopyKind.Original)
                {
                    var activeId = request.ActiveFileContainerId ?? paper.HomeActiveContainerId;
                    var active = activeId is null ? null : await _db.PhysicalFileContainers.FirstOrDefaultAsync(c => c.Id == activeId, ct);
                    if (active is null)
                    {
                        return OperationResult.Failure("AR 195-5 para 2-7b: name the active DA Form 4137 file the returned original goes into.", "FIL-005");
                    }

                    var assignment = voucher.CurrentDocumentNumberAssignment!;
                    paper.ReturnOriginalToActiveFile(active, suspense, assignment.Sequence, assignment.CalendarYear, _currentUser.UserId, request.ReturnedAtLocal,
                        $"Original returned by {returnedBy.DisplayName}, annotated; filed in {active.Label}.");
                }
                else
                {
                    paper.ReturnCopyFromEvidence(suspense, _currentUser.UserId, request.ReturnedAtLocal, $"Copy returned by {returnedBy.DisplayName}; chain recorded on the first copy.");
                }
            }
            else
            {
                warnings.Add($"{release.ItemsOut} item(s) remain out on this release; the {(release.PaperAccompanying == PaperCopyKind.Original ? "original" : "copy")} stays with them and the first copy stays in the suspense folder.");
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(TemporaryRelease), release.Id.ToString(),
            newValue: $"{returning.Count} item(s) returned; release now {release.Status}", reason: "AR 195-5 2-7b return from temporary release");

        var saved = await CommitAsync(ct);
        return saved ?? OperationResult.Success([.. warnings]);
    }

    public async Task<OperationResult> RecordNotReturnedAsync(NotReturnedRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var release = await _db.TemporaryReleases
            .Include(r => r.ReleasedBy).Include(r => r.ReceivedBy)
            .Include(r => r.Items).Include(r => r.Events).Include(r => r.Contacts)
            .FirstOrDefaultAsync(r => r.Id == request.TemporaryReleaseId, ct);
        if (release is null)
        {
            return OperationResult.Failure("Temporary release not found.", "SUSP-016");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ReturnFromTemporaryRelease, release.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(AuditEventType.PermissionDenied, nameof(TemporaryRelease), release.Id.ToString(), reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return OperationResult.Failure(decision.Reason!, decision.RequirementId);
        }

        if (request.ItemIds is null || request.ItemIds.Count == 0 || string.IsNullOrWhiteSpace(request.Narrative))
        {
            return OperationResult.Failure("Name the item(s) and say what became of them.", "SUSP-016");
        }

        if (request.Reason == NotReturnedReason.ConsumedOrRetainedByLaboratory)
        {
            if (release.Category != SuspenseCategory.Usacil)
            {
                return OperationResult.Failure("Consumption or retention by a laboratory applies to a laboratory release.", "SUSP-016");
            }

            if (string.IsNullOrWhiteSpace(request.MfrReference))
            {
                return OperationResult.Failure("AR 195-5 para 2-7c(2): an MFR explaining the circumstances is prepared and attached to the DA Form 4137. Record its reference.", "SUSP-016");
            }
        }

        if (request.Reason == NotReturnedReason.EnteredInRecordOfTrial && release.Category != SuspenseCategory.Adjudication)
        {
            return OperationResult.Failure("Entry in the record of trial follows a release for legal proceedings (ADJUDICATION).", "SUSP-016");
        }

        var voucher = await _db.EvidenceVouchers.Include(v => v.Items).FirstAsync(v => v.Id == release.VoucherId, ct);
        var now = _clock.UtcNow;
        try
        {
            foreach (var itemId in request.ItemIds.Distinct())
            {
                var member = release.Items.FirstOrDefault(m => m.EvidenceItemId == itemId);
                if (member is null || member.Status != TemporaryReleaseItemStatus.Out)
                {
                    return OperationResult.Failure($"Item {itemId} is not out on this release.", "SUSP-016");
                }

                var item = voucher.Items.First(i => i.Id == itemId);
                var reason = request.Reason == NotReturnedReason.EnteredInRecordOfTrial
                    ? $"Entered as a permanent part of the record of trial - final disposition (AR 195-5 3-1a(4), 2-8e(4)). {request.Narrative.Trim()}"
                    : $"Consumed in examination or retained by the laboratory (AR 195-5 2-7c(2)); MFR {request.MfrReference!.Trim()}. {request.Narrative.Trim()}";

                var from = item.AccountabilityStatus;
                item.TransitionTo(AccountabilityStatus.DispositionPending);
                await _events.AppendAsync(item, new StatusEvent(from, AccountabilityStatus.DispositionPending, reason, request.OccurredAtLocal, now, _currentUser.UserId), ct);
                release.RecordItemAccountedForWithoutReturn(itemId, request.OccurredAtLocal, now, _currentUser.UserId, reason);
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(TemporaryRelease), release.Id.ToString(),
            newValue: $"{request.ItemIds.Count} item(s) accounted for without return: {request.Reason}; release now {release.Status}", reason: "SUSP-016");

        var saved = await CommitAsync(ct);
        if (saved is not null)
        {
            return saved;
        }

        var warnings = new List<string>
        {
            "The item(s) are now DispositionPending: the Final Disposal Action portion of the DA Form 4137 is completed through the disposition workflow (AR 195-5 2-8, 2-9), which this application does not yet perform."
        };
        if (!release.IsOpen && release.PaperAccompanying == PaperCopyKind.Original)
        {
            warnings.Add(request.Reason == NotReturnedReason.EnteredInRecordOfTrial
                ? "AR 195-5 2-4g(1): the original DA Form 4137 is part of the record of trial; file a copy in the inactive file noting that, through the paper record."
                : "The original DA Form 4137 went with the specimens; record what became of it through the paper record (AR 195-5 2-4g).");
        }

        return OperationResult.Success([.. warnings]);
    }

    public async Task<OperationResult> RecordContactAsync(RecordSuspenseContactRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var release = await _db.TemporaryReleases.Include(r => r.Contacts).Include(r => r.Items).Include(r => r.Events)
            .FirstOrDefaultAsync(r => r.Id == request.TemporaryReleaseId, ct);
        if (release is null)
        {
            return OperationResult.Failure("Temporary release not found.", "SUSP-005");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ReleaseTemporarily, release.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(AuditEventType.PermissionDenied, nameof(SuspenseContact), release.Id.ToString(), reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return OperationResult.Failure(decision.Reason!, decision.RequirementId);
        }

        try
        {
            release.RecordContact(request.ContactedAtLocal, _clock.UtcNow, _currentUser.UserId, request.Method, request.ContactedPerson, request.Outcome, request.Narrative, request.NextFollowUpLocal);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(SuspenseContact), release.Id.ToString(),
            newValue: $"{request.Method}; outcome {request.Outcome}", reason: "AR 195-5 2-7a contact");
        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<TemporaryReleaseView?> GetAsync(int releaseId, CancellationToken ct = default)
    {
        var roomId = await _db.TemporaryReleases.AsNoTracking().Where(r => r.Id == releaseId).Select(r => (int?)r.EvidenceRoomId).FirstOrDefaultAsync(ct);
        if (roomId is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewEvidenceHistory, roomId, ct)).IsAllowed)
        {
            return null;
        }

        var release = await LoadAsync().FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        return release is null ? null : await ToViewAsync(release, ct);
    }

    public async Task<IReadOnlyList<TemporaryReleaseView>> GetForVoucherAsync(int voucherId, CancellationToken ct = default)
    {
        var roomId = await _db.EvidenceVouchers.AsNoTracking().Where(v => v.Id == voucherId).Select(v => (int?)v.EvidenceRoomId).FirstOrDefaultAsync(ct);
        if (roomId is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewEvidenceHistory, roomId, ct)).IsAllowed)
        {
            return [];
        }

        var releases = await LoadAsync().Where(r => r.VoucherId == voucherId).OrderByDescending(r => r.ReleasedAtUtc).ThenByDescending(r => r.Id).ToListAsync(ct);
        var views = new List<TemporaryReleaseView>(releases.Count);
        foreach (var r in releases)
        {
            views.Add(await ToViewAsync(r, ct));
        }

        return views;
    }

    private IQueryable<TemporaryRelease> LoadAsync()
        => _db.TemporaryReleases.AsNoTracking()
            .Include(r => r.ReleasedBy).Include(r => r.ReceivedBy)
            .Include(r => r.Items).Include(r => r.Events).Include(r => r.Contacts);

    private async Task<TemporaryReleaseView> ToViewAsync(TemporaryRelease r, CancellationToken ct)
    {
        var voucher = await _db.EvidenceVouchers.AsNoTracking().Include(v => v.DocumentNumberAssignments).Include(v => v.Items).FirstAsync(v => v.Id == r.VoucherId, ct);
        var folderLabel = await _db.PhysicalFileContainers.AsNoTracking().Where(c => c.Id == r.SuspenseFolderContainerId).Select(c => c.Label).FirstOrDefaultAsync(ct) ?? "(folder)";
        var userIds = r.Events.Select(e => e.RecordedByUserId).Concat(r.Contacts.Select(c => c.RecordedByUserId)).Append(r.RecordedByUserId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);
        var now = _clock.UtcNow;

        return new TemporaryReleaseView(
            r.Id, r.VoucherId, voucher.DisplayIdentifier, r.EvidenceRoomId, r.Category, r.Status, r.PaperAccompanying,
            r.ReleasedBy.DisplayName, r.ReceivedBy.DisplayName, r.ReceivedBy.Kind, r.ReceivedBy.OrganizationOrAgency,
            r.Purpose, r.Destination, r.ReleasedAtLocal, r.RecordedAtUtc, names.GetValueOrDefault(r.RecordedByUserId, "(unknown user)"),
            r.ExpectedFollowUpLocal, folderLabel,
            r.Attestations.PhysicalInventoryPerformedAttested, r.Attestations.Original4137ReceivedBySignedAttested, r.Attestations.FirstCopyReceivedBySignedAttested,
            r.Attestations.IdentificationPresentedAttested, r.Attestations.ObligationsInformedAttested,
            r.IsOpen ? r.DaysOut(now) : (int)Math.Max(0, Math.Floor(((r.ClosedAtUtc ?? now) - r.ReleasedAtUtc).TotalDays)),
            r.ItemsOut, r.LastContactAtUtc, r.ClosedAtUtc, r.Notes,
            r.Items.OrderBy(i => i.ItemNumber).Select(i => new TemporaryReleaseItemRow(i.EvidenceItemId, i.ItemNumber,
                voucher.Items.FirstOrDefault(v => v.Id == i.EvidenceItemId)?.DescriptionForForm ?? string.Empty, i.Status, i.ReleaseCustodyEventId, i.ReturnCustodyEventId, i.ReturnedAtUtc)).ToList(),
            r.Events.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id).Select(e => new TemporaryReleaseEventRow(e.Kind, e.OccurredAtUtc, e.RecordedAtUtc, names.GetValueOrDefault(e.RecordedByUserId, "(unknown user)"),
                e.EvidenceItemId is int eid ? r.Items.FirstOrDefault(i => i.EvidenceItemId == eid)?.ItemNumber : null, e.Narrative)).ToList(),
            r.Contacts.OrderBy(c => c.ContactedAtUtc).ThenBy(c => c.Id).Select(c => new SuspenseContactRow(c.ContactedAtLocal, c.RecordedAtUtc, names.GetValueOrDefault(c.RecordedByUserId, "(unknown user)"),
                c.Method, c.ContactedPerson, c.Outcome, c.Narrative, c.NextFollowUpLocal)).ToList(),
            r.Laboratory?.LaboratoryName, r.Laboratory?.CoordinatedWithUsacilAttested ?? false, r.Laboratory?.ExaminationRequestReference, r.Laboratory?.ShippingDocumentReference,
            r.OriginalAnnotatedOnReturnAttested, r.FirstCopyChainAnnotatedOnReturnAttested);
    }

    private static CustodyParty ToParty(ReleaseRecipient recipient)
        => recipient.Kind switch
        {
            CustodyPartyKind.ExternalPerson => CustodyParty.ForExternalPerson(recipient.Name, recipient.TitleOrGrade, recipient.OrganizationOrAgency, recipient.IdentificationPresented),
            CustodyPartyKind.Organization => CustodyParty.ForOrganization(recipient.Name),
            CustodyPartyKind.AccountableMailNumber => CustodyParty.ForAccountableMailNumber(recipient.AccountableMailNumber ?? recipient.Name, recipient.Carrier),
            CustodyPartyKind.InternalUser => throw new DomainRuleViolationException("SUSP-003", "An internal user is not a temporary-release recipient; evidence leaves the room to an external person, an organization, or accountable mail (2-7a, 2-7e)."),
            _ => throw new DomainRuleViolationException("SUSP-003", "The recipient kind is not one a temporary release accepts.")
        };
}
