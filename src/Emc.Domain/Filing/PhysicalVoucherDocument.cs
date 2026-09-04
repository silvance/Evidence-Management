using Emc.Domain.Common;

namespace Emc.Domain.Filing;

/// <summary>
/// What became of the PHYSICAL ORIGINAL DA Form 4137. Historical: once the original leaves this
/// room (2-7g, 2-4g) nothing this room later does to the paper it retained changes this.
/// </summary>
public enum OriginalDisposition
{
    /// <summary>Not yet filed. 2-4d: the custodian keeps the original after numbering.</summary>
    NotYetFiled = 0,

    /// <summary>AR 195-5 2-4f(1) - in this room's active DA Form 4137 file.</summary>
    HeldActive = 1,

    /// <summary>AR 195-5 2-4f(2), 2-7b - accompanies evidence on temporary release; the first copy is in suspense.</summary>
    AccompanyingTemporaryRelease = 2,

    /// <summary>AR 195-5 2-4f(3)(c) - with trial counsel / prosecutor for disposition approval; a copy is in suspense.</summary>
    SentForDispositionApproval = 3,

    /// <summary>AR 195-5 2-4h (or closure under 3-3c) - in this room's inactive file.</summary>
    FiledInactive = 4,

    /// <summary>AR 195-5 2-7g - the original and duplicate went with the evidence to the gaining room.</summary>
    TransferredToGainingRoom = 5,

    /// <summary>AR 195-5 2-4g(1) - a permanent part of the record of trial.</summary>
    PartOfRecordOfTrial = 6,

    /// <summary>AR 195-5 2-4g(2) - accompanied evidence released to an external agency.</summary>
    WithExternalAgency = 7,

    /// <summary>AR 195-5 2-4g(3) - not available for another documented reason.</summary>
    UnavailableOther = 8
}

/// <summary>
/// What THIS evidence room holds on paper for the voucher, and where. Operational: this is what
/// is in the binders and folders now. Separate from <see cref="OriginalDisposition"/> so that
/// destroying the paper this room retained (2-4h, three years after inactive) never rewrites
/// what became of an original that left.
/// </summary>
public enum RetainedPaperStatus
{
    /// <summary>Nothing filed yet.</summary>
    None = 0,

    /// <summary>The original, in an active folder/binder (2-4f(1)).</summary>
    ActiveOriginal = 1,

    /// <summary>The original is out; this room holds the first copy in a suspense folder (2-4f(2), 2-4f(3)).</summary>
    SuspenseCopy = 2,

    /// <summary>The original, in this room's inactive file (2-4h).</summary>
    InactiveOriginal = 3,

    /// <summary>A copy in this room's inactive file, noting the disposition of the original (2-4g, 2-7g, 2-4d).</summary>
    InactiveCopy = 4,

    /// <summary>This room's retained paper was destroyed after its retention period, confirmed by a person (2-4h).</summary>
    Destroyed = 5
}

/// <summary>Why the evidence room holds only a COPY in its inactive file (AR 195-5 2-4g, 2-7g).</summary>
public enum CopyRetentionReason
{
    None = 0,
    OriginalInRecordOfTrial = 1,
    OriginalWithExternalAgency = 2,
    OriginalUnavailableOther = 3,
    OriginalTransferredToGainingRoom = 4
}

/// <summary>Retention status of the evidence room's INACTIVE paper record (AR 195-5 2-4h). Paper only.</summary>
public enum PaperRetentionStatus
{
    /// <summary>Not inactive, or inactive for less than three years.</summary>
    Retain = 1,

    /// <summary>Three years have passed since the record became inactive. Eligible; nothing is destroyed automatically.</summary>
    EligibleForDestruction = 2,

    /// <summary>The custodian recorded that the paper record was actually destroyed.</summary>
    DestructionConfirmed = 3
}

public enum PhysicalDocumentEventKind
{
    OriginalFiledActive = 1,
    OriginalAccompaniesTemporaryRelease = 2,
    SuspenseCopyRetained = 3,
    OriginalReturnedToActiveFile = 4,
    OriginalSentForDispositionApproval = 5,
    OriginalFiledInactive = 6,
    OriginalTransferredToGainingRoom = 7,
    SendingRoomCopyFiledInactive = 8,
    CopyFiledInactiveOriginalInRecordOfTrial = 9,
    CopyFiledInactiveOriginalWithExternalAgency = 10,
    CopyFiledOriginalUnavailable = 11,
    DestructionConfirmed = 12,
    Note = 13,

    /// <summary>AR 195-5 2-7b - on return, the first (suspense) copy, chain of custody annotated, is filed with the original.</summary>
    SuspenseCopyFiledWithOriginal = 14
}

/// <summary>
/// The evidence room's record of the PHYSICAL DA Form 4137 for one voucher, on two axes:
///
///   <see cref="OriginalDisposition"/>  what became of the original (historical; never rewritten
///                                       by what this room later does with its own paper);
///   <see cref="RetainedPaperStatus"/>  what this room holds now and where (operational).
///
/// Where the paper physically is now is <see cref="CurrentContainerId"/>: the active binder
/// while the original is here, the suspense folder while only the first copy is here, the
/// inactive file afterwards, nothing once destroyed. <see cref="HomeActiveContainerId"/> is the
/// active binder the original belongs in (by its document-number range, 2-4f(1)) and returns to
/// (2-7b). While the original is out, the binder does not contain it and does not count it.
///
/// A voucher-level record, deliberately. The DA Form 4137 is one document listing many items; it
/// is filed, released, returned and retired as a whole (2-4d, 2-4f, 2-4h). It is not the digital
/// scan: a <c>SourceDocument</c> is a companion copy; this says where the paper is (FIL-004).
///
/// THE THREE-YEAR CLOCK (2-4h) runs from the date this room's paper record BECAME INACTIVE. It
/// does not know the case's status. Eligibility is computed; nothing is destroyed by software;
/// "eligible" and "destroyed" are different states (FIL-006, FIL-009). EMC's digital records are
/// retained regardless (DEC-07).
///
/// Requirements: FIL-004 .. FIL-014, SUSP-007 (paper portion), RET-007 (paper portion).
/// </summary>
public class PhysicalVoucherDocument : Entity, IConcurrencyStamped
{
    /// <summary>AR 195-5 2-4h: "disposed of 3 years after the date they become inactive." [REG]</summary>
    public const int InactiveRetentionYears = 3;

    private readonly List<PhysicalVoucherDocumentEvent> _events = [];

    private PhysicalVoucherDocument() { }

    public PhysicalVoucherDocument(int voucherId, int evidenceRoomId)
    {
        VoucherId = voucherId;
        EvidenceRoomId = evidenceRoomId;
        OriginalDisposition = OriginalDisposition.NotYetFiled;
        RetainedPaperStatus = RetainedPaperStatus.None;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int VoucherId { get; private set; }
    public int EvidenceRoomId { get; private set; }

    public OriginalDisposition OriginalDisposition { get; private set; }
    public RetainedPaperStatus RetainedPaperStatus { get; private set; }

    /// <summary>The container this room's retained paper is in NOW. Null before filing and after destruction.</summary>
    public int? CurrentContainerId { get; private set; }

    /// <summary>The active binder the original belongs to and returns to (2-4f(1), 2-7b). Null once inactive.</summary>
    public int? HomeActiveContainerId { get; private set; }

    public CopyRetentionReason CopyReason { get; private set; }

    /// <summary>AR 195-5 2-7b - the returned first copy, chain of custody annotated, is filed with the original.</summary>
    public bool SuspenseCopyFiledWithOriginal { get; private set; }

    /// <summary>When this room's paper record became inactive. The 2-4h clock starts here.</summary>
    public DateTimeOffset? InactiveSinceUtc { get; private set; }

    public DateTimeOffset? DestructionConfirmedAtUtc { get; private set; }
    public int? DestructionConfirmedByUserId { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyList<PhysicalVoucherDocumentEvent> Events => _events.AsReadOnly();

    /// <summary>True when this room holds only a copy of the form (2-4g, 2-7g).</summary>
    public bool HoldsCopyOnly => RetainedPaperStatus is RetainedPaperStatus.InactiveCopy or RetainedPaperStatus.SuspenseCopy;

    public bool OriginalIsOut
        => OriginalDisposition is OriginalDisposition.AccompanyingTemporaryRelease or OriginalDisposition.SentForDispositionApproval;

    /// <summary>The original left this room for good (2-7g, 2-4g).</summary>
    public bool OriginalLeftThisRoom
        => OriginalDisposition is OriginalDisposition.TransferredToGainingRoom or OriginalDisposition.PartOfRecordOfTrial
            or OriginalDisposition.WithExternalAgency or OriginalDisposition.UnavailableOther;

    public bool IsInactive => InactiveSinceUtc is not null;

    /// <summary>AR 195-5 2-4h - exactly three years after the record became inactive. Null while active.</summary>
    public DateTimeOffset? DestructionEligibleAtUtc => InactiveSinceUtc?.AddYears(InactiveRetentionYears);

    public PaperRetentionStatus RetentionStatusAt(DateTimeOffset at)
    {
        if (DestructionConfirmedAtUtc is not null)
        {
            return PaperRetentionStatus.DestructionConfirmed;
        }

        return DestructionEligibleAtUtc is { } eligible && at >= eligible
            ? PaperRetentionStatus.EligibleForDestruction
            : PaperRetentionStatus.Retain;
    }

    // ----- lifecycle -----------------------------------------------------------------------

    /// <summary>
    /// AR 195-5 2-4d, 2-4f(1) - the custodian retains the original and files it in the active
    /// folder/binder whose document-number range covers this voucher's number.
    /// </summary>
    public void FileOriginalInActiveFile(PhysicalFileContainer container, int sequence, int calendarYear, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(container, PhysicalFileKind.Active4137File);
        RequireOriginal("FIL-004", "file the original in the active file", OriginalDisposition.NotYetFiled);
        container.AssertCoversDocumentNumber(sequence, calendarYear);
        container.RecordFiled();

        OriginalDisposition = OriginalDisposition.HeldActive;
        RetainedPaperStatus = RetainedPaperStatus.ActiveOriginal;
        CurrentContainerId = container.Id;
        HomeActiveContainerId = container.Id;
        Add(PhysicalDocumentEventKind.OriginalFiledActive, userId, at, container.Id, narrative);
    }

    /// <summary>
    /// AR 195-5 2-4f(2), 2-7b: the original accompanies the evidence; the first copy goes in the
    /// proper suspense folder (USACIL or ADJUDICATION, 2-4f(3)). The active binder no longer
    /// contains the original and no longer counts it; it stays the original's home.
    /// </summary>
    public void ReleaseOriginalWithEvidence(PhysicalFileContainer homeActiveFile, PhysicalFileContainer suspenseFolder, int userId, DateTimeOffset at, string? narrative = null)
    {
        ArgumentNullException.ThrowIfNull(suspenseFolder);
        RequireSameRoom(suspenseFolder);

        if (suspenseFolder.Kind is not (PhysicalFileKind.SuspenseUsacil or PhysicalFileKind.SuspenseAdjudication))
        {
            throw new DomainRuleViolationException(
                "FIL-005",
                "AR 195-5 para 2-4f(3): the suspense copy for evidence on temporary release goes in "
                + "the USACIL folder (evidence sent to the laboratory) or the ADJUDICATION folder "
                + "(legal proceedings). The PENDING DISPOSITION APPROVAL folder is for an original "
                + "sent for disposition approval.");
        }

        RequireOriginal("FIL-005", "release the original with the evidence", OriginalDisposition.HeldActive);
        RequireHome(homeActiveFile);
        suspenseFolder.RecordFiled();
        homeActiveFile.RecordRemoved();

        OriginalDisposition = OriginalDisposition.AccompanyingTemporaryRelease;
        RetainedPaperStatus = RetainedPaperStatus.SuspenseCopy;
        CurrentContainerId = suspenseFolder.Id;
        SuspenseCopyFiledWithOriginal = false;
        Add(PhysicalDocumentEventKind.OriginalAccompaniesTemporaryRelease, userId, at, null, narrative);
        Add(PhysicalDocumentEventKind.SuspenseCopyRetained, userId, at, suspenseFolder.Id, null);
    }

    /// <summary>
    /// AR 195-5 2-7b: when the evidence is returned, the original, properly annotated, is put in
    /// the appropriate active file, and the first (suspense) copy, chain of custody annotated, is
    /// filed WITH the original. The suspense folder no longer holds the copy.
    /// </summary>
    public void ReturnOriginalToActiveFile(PhysicalFileContainer activeFile, PhysicalFileContainer suspenseFolder, int sequence, int calendarYear, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(activeFile, PhysicalFileKind.Active4137File);
        RequireOriginal("FIL-005", "return the original to the active file",
            OriginalDisposition.AccompanyingTemporaryRelease, OriginalDisposition.SentForDispositionApproval);
        RequireCurrent(suspenseFolder);
        activeFile.AssertCoversDocumentNumber(sequence, calendarYear);
        activeFile.RecordFiled();
        suspenseFolder.RecordRemoved();

        OriginalDisposition = OriginalDisposition.HeldActive;
        RetainedPaperStatus = RetainedPaperStatus.ActiveOriginal;
        CurrentContainerId = activeFile.Id;
        HomeActiveContainerId = activeFile.Id;
        SuspenseCopyFiledWithOriginal = true;
        Add(PhysicalDocumentEventKind.OriginalReturnedToActiveFile, userId, at, activeFile.Id, narrative);
        Add(PhysicalDocumentEventKind.SuspenseCopyFiledWithOriginal, userId, at, activeFile.Id, "AR 195-5 2-7b: first copy, chain of custody annotated, filed with the original.");
    }

    /// <summary>AR 195-5 2-4f(3)(c), 2-8e(5) - the original goes to trial counsel / prosecutor; the copy waits in PENDING DISPOSITION APPROVAL. Evidence stays in the room.</summary>
    public void SendOriginalForDispositionApproval(PhysicalFileContainer homeActiveFile, PhysicalFileContainer pendingFolder, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(pendingFolder, PhysicalFileKind.SuspensePendingDispositionApproval);
        RequireOriginal("FIL-005", "send the original for disposition approval", OriginalDisposition.HeldActive);
        RequireHome(homeActiveFile);
        pendingFolder.RecordFiled();
        homeActiveFile.RecordRemoved();

        OriginalDisposition = OriginalDisposition.SentForDispositionApproval;
        RetainedPaperStatus = RetainedPaperStatus.SuspenseCopy;
        CurrentContainerId = pendingFolder.Id;
        SuspenseCopyFiledWithOriginal = false;
        Add(PhysicalDocumentEventKind.OriginalSentForDispositionApproval, userId, at, null, narrative);
        Add(PhysicalDocumentEventKind.SuspenseCopyRetained, userId, at, pendingFolder.Id, null);
    }

    /// <summary>
    /// The original goes to this room's inactive file. Two bases, cited separately:
    ///   2-4h  after ALL items listed on the form have been properly disposed;
    ///   3-3c  relief from accountability, which "permits the closure of the DA Form 4137".
    /// A form whose items were permanently transferred is NOT filed this way: the original went
    /// with the evidence (2-7g); the sending room files a COPY. The caller supplies the voucher's
    /// closure basis. The inactive file's month and year must be those of the inactive date.
    /// </summary>
    public void FileOriginalInactive(PhysicalFileContainer inactiveFile, PhysicalFileContainer? currentHolder, VoucherClosureBasis closureBasis, int userId, DateTimeOffset inactiveAt, string? narrative = null)
    {
        RequireContainer(inactiveFile, PhysicalFileKind.Inactive4137File);
        RequireOriginal("FIL-006", "file the original as inactive",
            OriginalDisposition.HeldActive, OriginalDisposition.SentForDispositionApproval, OriginalDisposition.NotYetFiled);

        switch (closureBasis)
        {
            case VoucherClosureBasis.AllItemsFinallyDisposed:
            case VoucherClosureBasis.AllItemsReliefGranted:
            case VoucherClosureBasis.MixedDisposedAndReliefGranted:
                break;
            case VoucherClosureBasis.AllItemsPermanentlyTransferred:
            case VoucherClosureBasis.MixedIncludingPermanentTransfer:
                throw new DomainRuleViolationException(
                    "FIL-006",
                    "AR 195-5 para 2-7g: on permanent transfer the original and duplicate DA Form 4137 go "
                    + "with the evidence and the sending room places a COPY showing the disposition in its "
                    + "inactive file. Record the transfer of the original, not an inactive filing of it.");
            default:
                throw new DomainRuleViolationException(
                    "FIL-006",
                    "AR 195-5 para 2-4h: the original DA Form 4137 moves to the inactive file after ALL items "
                    + "listed on it have been properly disposed (or, under 3-3c, relief from accountability "
                    + "has been granted). This voucher still has items accounted for in this room.");
        }

        inactiveFile.AssertLabeledForDispositionDate(inactiveAt);
        if (currentHolder is not null)
        {
            RequireCurrent(currentHolder);
            currentHolder.RecordRemoved();
        }

        inactiveFile.RecordFiled();

        OriginalDisposition = OriginalDisposition.FiledInactive;
        RetainedPaperStatus = RetainedPaperStatus.InactiveOriginal;
        CurrentContainerId = inactiveFile.Id;
        HomeActiveContainerId = null;
        InactiveSinceUtc = AccountabilityTime.Normalize(inactiveAt);
        Add(PhysicalDocumentEventKind.OriginalFiledInactive, userId, inactiveAt, inactiveFile.Id,
            closureBasis == VoucherClosureBasis.AllItemsFinallyDisposed ? narrative : $"Closure basis {closureBasis} (AR 195-5 3-3c where relief was granted). {narrative}".Trim());
    }

    /// <summary>
    /// AR 195-5 2-7g, 2-4d - on permanent transfer the original and duplicate go with the
    /// evidence to the gaining room; the sending room retains a copy showing the disposition in
    /// its inactive file. Ends this room's paper accountability; the investigation may continue.
    /// </summary>
    public void TransferOriginalToGainingRoom(PhysicalFileContainer inactiveFile, PhysicalFileContainer? currentHolder, VoucherClosureBasis closureBasis, string gainingEvidenceRoom, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(inactiveFile, PhysicalFileKind.Inactive4137File);
        RequireOriginal("FIL-007", "transfer the original to the gaining evidence room",
            OriginalDisposition.HeldActive, OriginalDisposition.NotYetFiled);
        if (closureBasis != VoucherClosureBasis.AllItemsPermanentlyTransferred)
        {
            throw new DomainRuleViolationException(
                "FIL-007",
                "AR 195-5 para 2-7g: the original and duplicate DA Form 4137 accompany the evidence on "
                + "permanent transfer. Record the items' permanent transfer first; this voucher still has "
                + "items accounted for in this room, or closed on another basis.");
        }

        var gaining = Guard.NotBlank(gainingEvidenceRoom, "FIL-007", "Gaining evidence room");
        inactiveFile.AssertLabeledForDispositionDate(at);
        if (currentHolder is not null)
        {
            RequireCurrent(currentHolder);
            currentHolder.RecordRemoved();
        }

        inactiveFile.RecordFiled();

        OriginalDisposition = OriginalDisposition.TransferredToGainingRoom;
        RetainedPaperStatus = RetainedPaperStatus.InactiveCopy;
        CopyReason = CopyRetentionReason.OriginalTransferredToGainingRoom;
        CurrentContainerId = inactiveFile.Id;
        HomeActiveContainerId = null;
        InactiveSinceUtc = AccountabilityTime.Normalize(at);
        Add(PhysicalDocumentEventKind.OriginalTransferredToGainingRoom, userId, at, null, $"To {gaining}. {narrative}".Trim());
        Add(PhysicalDocumentEventKind.SendingRoomCopyFiledInactive, userId, at, inactiveFile.Id, null);
    }

    /// <summary>
    /// AR 195-5 2-4g - a COPY is used as the suspense copy and placed in the inactive file, noting
    /// the disposition of the original: record of trial (1), external agency (2), unavailable for
    /// other reasons (3). The clock starts now.
    /// </summary>
    public void FileCopyInactiveBecauseOriginalUnavailable(
        PhysicalFileContainer inactiveFile, PhysicalFileContainer? currentHolder, CopyRetentionReason reason, string dispositionOfOriginal, int userId, DateTimeOffset at)
    {
        RequireContainer(inactiveFile, PhysicalFileKind.Inactive4137File);

        if (reason is not (CopyRetentionReason.OriginalInRecordOfTrial
            or CopyRetentionReason.OriginalWithExternalAgency
            or CopyRetentionReason.OriginalUnavailableOther))
        {
            throw new DomainRuleViolationException(
                "FIL-008", "State which of AR 195-5 para 2-4g(1), (2) or (3) applies.");
        }

        RequireOriginal("FIL-008", "file a copy as inactive because the original is unavailable",
            OriginalDisposition.HeldActive, OriginalDisposition.AccompanyingTemporaryRelease,
            OriginalDisposition.SentForDispositionApproval, OriginalDisposition.NotYetFiled);

        var disposition = Guard.NotBlank(dispositionOfOriginal, "FIL-008", "Disposition of the original (AR 195-5 2-4g)");
        inactiveFile.AssertLabeledForDispositionDate(at);
        if (currentHolder is not null)
        {
            RequireCurrent(currentHolder);
            currentHolder.RecordRemoved();
        }

        inactiveFile.RecordFiled();

        OriginalDisposition = reason switch
        {
            CopyRetentionReason.OriginalInRecordOfTrial => OriginalDisposition.PartOfRecordOfTrial,
            CopyRetentionReason.OriginalWithExternalAgency => OriginalDisposition.WithExternalAgency,
            _ => OriginalDisposition.UnavailableOther
        };
        RetainedPaperStatus = RetainedPaperStatus.InactiveCopy;
        CopyReason = reason;
        CurrentContainerId = inactiveFile.Id;
        HomeActiveContainerId = null;
        InactiveSinceUtc = AccountabilityTime.Normalize(at);

        var kind = reason switch
        {
            CopyRetentionReason.OriginalInRecordOfTrial => PhysicalDocumentEventKind.CopyFiledInactiveOriginalInRecordOfTrial,
            CopyRetentionReason.OriginalWithExternalAgency => PhysicalDocumentEventKind.CopyFiledInactiveOriginalWithExternalAgency,
            _ => PhysicalDocumentEventKind.CopyFiledOriginalUnavailable
        };
        Add(kind, userId, at, inactiveFile.Id, disposition);
    }

    /// <summary>
    /// The custodian confirms THIS ROOM'S inactive paper was actually destroyed. Changes only the
    /// retained-paper axis: what became of the original is history and stays as recorded
    /// (FIL-014). Nothing here touches EMC's digital records, the scan, or the case-file copy
    /// (FIL-009, DEC-07).
    /// </summary>
    public void ConfirmDestruction(PhysicalFileContainer? inactiveHolder, int userId, DateTimeOffset at, string narrative)
    {
        if (!IsInactive)
        {
            throw new DomainRuleViolationException(
                "FIL-009", "Only an inactive paper record can be destroyed (AR 195-5 para 2-4h).");
        }

        if (RetentionStatusAt(at) == PaperRetentionStatus.DestructionConfirmed)
        {
            throw new DomainRuleViolationException("FIL-009", "Destruction has already been confirmed.");
        }

        if (RetentionStatusAt(at) != PaperRetentionStatus.EligibleForDestruction)
        {
            throw new DomainRuleViolationException(
                "FIL-009",
                $"AR 195-5 para 2-4h: inactive DA Forms 4137 are disposed of 3 years after the date "
                + $"they become inactive. This record became inactive on "
                + $"{InactiveSinceUtc:yyyy-MM-dd} and is eligible on {DestructionEligibleAtUtc:yyyy-MM-dd}.");
        }

        var what = Guard.NotBlank(narrative, "FIL-009", "Destruction record (how, when, by whom)");
        if (inactiveHolder is not null)
        {
            RequireCurrent(inactiveHolder);
            inactiveHolder.RecordRemoved();
        }

        var originalBefore = OriginalDisposition;
        DestructionConfirmedAtUtc = AccountabilityTime.Normalize(at);
        DestructionConfirmedByUserId = userId;
        RetainedPaperStatus = RetainedPaperStatus.Destroyed;
        CurrentContainerId = null;
        Add(PhysicalDocumentEventKind.DestructionConfirmed, userId, at, null, what);

        if (OriginalDisposition != originalBefore)
        {
            throw new InvalidOperationException("Invariant FIL-014: destruction of retained paper must not change the original's disposition.");
        }
    }

    public void AddNote(int userId, DateTimeOffset at, string narrative)
        => Add(PhysicalDocumentEventKind.Note, userId, at, null, Guard.NotBlank(narrative, "FIL-004", "Note"));

    private void Add(PhysicalDocumentEventKind kind, int userId, DateTimeOffset at, int? containerId, string? narrative)
        => _events.Add(new PhysicalVoucherDocumentEvent(this, kind, OriginalDisposition, RetainedPaperStatus, userId, at, containerId, narrative));

    private void RequireContainer(PhysicalFileContainer container, PhysicalFileKind kind)
    {
        ArgumentNullException.ThrowIfNull(container);
        RequireSameRoom(container);

        if (container.Kind != kind)
        {
            throw new DomainRuleViolationException(
                "FIL-001", $"\"{container.Label}\" is a {container.Kind} file; this action needs a {kind}.");
        }
    }

    private void RequireSameRoom(PhysicalFileContainer container)
    {
        if (container.EvidenceRoomId != EvidenceRoomId)
        {
            throw new DomainRuleViolationException(
                "FIL-001", "A DA Form 4137 is filed in its own evidence room's files.");
        }
    }

    private void RequireHome(PhysicalFileContainer homeActiveFile)
    {
        ArgumentNullException.ThrowIfNull(homeActiveFile);
        if (homeActiveFile.Id != HomeActiveContainerId || homeActiveFile.Id != CurrentContainerId)
        {
            throw new DomainRuleViolationException("FIL-001", "The active binder given is not the one holding this original.");
        }
    }

    private void RequireCurrent(PhysicalFileContainer holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (holder.Id != CurrentContainerId)
        {
            throw new DomainRuleViolationException("FIL-001", $"\"{holder.Label}\" does not currently hold this voucher's paper.");
        }
    }

    private void RequireOriginal(string requirementId, string action, params OriginalDisposition[] allowed)
    {
        if (!allowed.Contains(OriginalDisposition))
        {
            throw new DomainRuleViolationException(
                requirementId,
                $"Cannot {action}: the original DA Form 4137 is {OriginalDisposition}.");
        }
    }
}

/// <summary>One thing that happened to the paper record. Append-only; records both axes as they stood after it.</summary>
public class PhysicalVoucherDocumentEvent : Entity, IAppendOnly
{
    private PhysicalVoucherDocumentEvent() { }

    internal PhysicalVoucherDocumentEvent(
        PhysicalVoucherDocument document,
        PhysicalDocumentEventKind kind,
        OriginalDisposition resultingOriginalDisposition,
        RetainedPaperStatus resultingRetainedPaperStatus,
        int recordedByUserId,
        DateTimeOffset occurredAtUtc,
        int? containerId,
        string? narrative)
    {
        DocumentId = document.Id;
        Document = document;
        Kind = kind;
        ResultingOriginalDisposition = resultingOriginalDisposition;
        ResultingRetainedPaperStatus = resultingRetainedPaperStatus;
        RecordedByUserId = recordedByUserId;
        OccurredAtUtc = AccountabilityTime.Normalize(occurredAtUtc);
        ContainerId = containerId;
        Narrative = Guard.TrimToNull(narrative);
    }

    public int DocumentId { get; private set; }
    public PhysicalVoucherDocument? Document { get; private set; }
    public PhysicalDocumentEventKind Kind { get; private set; }
    public OriginalDisposition ResultingOriginalDisposition { get; private set; }
    public RetainedPaperStatus ResultingRetainedPaperStatus { get; private set; }
    public int RecordedByUserId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public int? ContainerId { get; private set; }
    public string? Narrative { get; private set; }
}
