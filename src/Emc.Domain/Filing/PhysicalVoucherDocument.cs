using Emc.Domain.Common;

namespace Emc.Domain.Filing;

/// <summary>Where the PHYSICAL ORIGINAL DA Form 4137 is, or what became of it.</summary>
public enum PhysicalOriginalStatus
{
    /// <summary>The original has not yet been filed. 2-4d: the custodian retains it after numbering.</summary>
    NotYetFiled = 0,

    /// <summary>AR 195-5 2-4f(1) - in the active DA Form 4137 file.</summary>
    FiledActive = 1,

    /// <summary>AR 195-5 2-4f(2) - accompanies evidence on temporary release; a copy is in suspense.</summary>
    AccompanyingTemporaryRelease = 2,

    /// <summary>AR 195-5 2-4f(3)(c) - sent to trial counsel / prosecutor for disposition approval; a copy is in suspense.</summary>
    SentForDispositionApproval = 3,

    /// <summary>AR 195-5 2-4h - in the inactive file after all items were disposed.</summary>
    FiledInactive = 4,

    /// <summary>AR 195-5 2-7g - the original (and duplicate) went with the evidence to the gaining room; this room keeps a copy.</summary>
    TransferredToGainingRoom = 5,

    /// <summary>AR 195-5 2-4g(1) - the original is a permanent part of the record of trial; this room keeps a copy.</summary>
    PartOfRecordOfTrial = 6,

    /// <summary>AR 195-5 2-4g(2) - the original accompanied evidence released to an external agency; this room keeps a copy.</summary>
    WithExternalAgency = 7,

    /// <summary>AR 195-5 2-4g(3) - the original is not available for another documented reason; this room keeps a copy.</summary>
    UnavailableOther = 8,

    /// <summary>The inactive paper record was destroyed, confirmed by the custodian. Terminal.</summary>
    Destroyed = 9
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
    Note = 13
}

/// <summary>
/// The evidence room's record of the PHYSICAL DA Form 4137 for one voucher: where the original
/// is, whether the room holds only a copy and why, when the paper record became inactive, and
/// when it was actually destroyed.
///
/// A voucher-level record, deliberately. The DA Form 4137 is one document listing many items; it
/// is filed, released, returned and retired as a whole (2-4d, 2-4f, 2-4h). It is not attached to
/// items.
///
/// It is NOT the digital scan. A <c>SourceDocument</c> is a companion copy with provenance saying
/// what paper was scanned; this record says where the paper is. Uploading a scan changes nothing
/// here, and a scan never satisfies 2-4f's requirement to maintain the original in the active
/// file (FIL-004).
///
/// THE THREE-YEAR CLOCK (2-4h) runs from the date the paper record BECAME INACTIVE - not from
/// the form's preparation, the evidence's receipt, or the case's status. This record does not
/// know whether the investigation is open, and that is correct: permanent transfer (2-7g) ends
/// the sending room's accountability while the investigation continues. Eligibility is computed;
/// nothing is destroyed by software, and "eligible" and "destroyed" are different states
/// (FIL-006, FIL-007). The copy in the investigative case file is a different record under a
/// different schedule and is not touched by any of this (FIL-008). EMC's own digital records are
/// retained regardless (DEC-07).
///
/// Requirements: FIL-004 .. FIL-009, SUSP-007 (paper portion), RET-007 (paper portion).
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
        OriginalStatus = PhysicalOriginalStatus.NotYetFiled;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int VoucherId { get; private set; }
    public int EvidenceRoomId { get; private set; }

    public PhysicalOriginalStatus OriginalStatus { get; private set; }

    /// <summary>The container the ORIGINAL is filed in, when it is filed here.</summary>
    public int? OriginalContainerId { get; private set; }

    /// <summary>The suspense folder holding the COPY while the original is out (2-4f(2), 2-4f(3)).</summary>
    public int? SuspenseCopyContainerId { get; private set; }

    /// <summary>The inactive file holding this room's paper record - original or copy (2-4h, 2-4g).</summary>
    public int? InactiveContainerId { get; private set; }

    /// <summary>True when this room holds only a copy; <see cref="CopyReason"/> says why.</summary>
    public bool HoldsCopyOnly { get; private set; }

    public CopyRetentionReason CopyReason { get; private set; }

    /// <summary>When this room's paper record became inactive. The 2-4h clock starts here.</summary>
    public DateTimeOffset? InactiveSinceUtc { get; private set; }

    public DateTimeOffset? DestructionConfirmedAtUtc { get; private set; }
    public int? DestructionConfirmedByUserId { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyList<PhysicalVoucherDocumentEvent> Events => _events.AsReadOnly();

    /// <summary>True while the original is held by this room in a file (active or inactive).</summary>
    public bool OriginalHeldHere
        => OriginalStatus is PhysicalOriginalStatus.FiledActive or PhysicalOriginalStatus.FiledInactive;

    public bool OriginalIsOut
        => OriginalStatus is PhysicalOriginalStatus.AccompanyingTemporaryRelease
            or PhysicalOriginalStatus.SentForDispositionApproval;

    public bool IsInactive => InactiveSinceUtc is not null;

    /// <summary>AR 195-5 2-4h - exactly three years after the record became inactive. Null while active.</summary>
    public DateTimeOffset? DestructionEligibleAtUtc => InactiveSinceUtc?.AddYears(InactiveRetentionYears);

    /// <summary>
    /// Paper retention status at <paramref name="at"/>. Computed, never stored; depends on
    /// nothing but the inactive date and whether destruction was confirmed.
    /// </summary>
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

    /// <summary>AR 195-5 2-4d, 2-4f(1) - the custodian retains the original and files it in the active file.</summary>
    public void FileOriginalInActiveFile(PhysicalFileContainer container, int currentlyFiledInContainer, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(container, PhysicalFileKind.Active4137File);
        RequireStatus("FIL-004", "file the original in the active file",
            PhysicalOriginalStatus.NotYetFiled);
        container.AssertCanAcceptAnotherVoucher(currentlyFiledInContainer);

        OriginalStatus = PhysicalOriginalStatus.FiledActive;
        OriginalContainerId = container.Id;
        Add(PhysicalDocumentEventKind.OriginalFiledActive, userId, at, container.Id, narrative);
    }

    /// <summary>
    /// AR 195-5 2-4f(2): "When evidence is temporarily released ... the original DA Form 4137
    /// will accompany the evidence and a copy ... retained in a suspense folder." Two events: the
    /// original goes; the copy stays in the named suspense folder (USACIL or ADJUDICATION, 2-4f(3)).
    /// </summary>
    public void ReleaseOriginalWithEvidence(PhysicalFileContainer suspenseFolder, int userId, DateTimeOffset at, string? narrative = null)
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

        RequireStatus("FIL-005", "release the original with the evidence", PhysicalOriginalStatus.FiledActive);
        suspenseFolder.AssertCanAcceptAnotherVoucher(0);

        OriginalStatus = PhysicalOriginalStatus.AccompanyingTemporaryRelease;
        SuspenseCopyContainerId = suspenseFolder.Id;
        Add(PhysicalDocumentEventKind.OriginalAccompaniesTemporaryRelease, userId, at, null, narrative);
        Add(PhysicalDocumentEventKind.SuspenseCopyRetained, userId, at, suspenseFolder.Id, null);
    }

    /// <summary>AR 195-5 2-4f(2) - "until the evidence is returned to the evidence room." The original comes back to its active file.</summary>
    public void ReturnOriginalToActiveFile(PhysicalFileContainer activeFile, int currentlyFiledInContainer, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(activeFile, PhysicalFileKind.Active4137File);
        RequireStatus("FIL-005", "return the original to the active file",
            PhysicalOriginalStatus.AccompanyingTemporaryRelease, PhysicalOriginalStatus.SentForDispositionApproval);

        if (activeFile.Id != OriginalContainerId)
        {
            activeFile.AssertCanAcceptAnotherVoucher(currentlyFiledInContainer);
        }

        OriginalStatus = PhysicalOriginalStatus.FiledActive;
        OriginalContainerId = activeFile.Id;
        SuspenseCopyContainerId = null;
        Add(PhysicalDocumentEventKind.OriginalReturnedToActiveFile, userId, at, activeFile.Id, narrative);
    }

    /// <summary>AR 195-5 2-4f(3)(c), 2-8e(5) - the original goes to trial counsel / prosecutor; the copy waits in PENDING DISPOSITION APPROVAL.</summary>
    public void SendOriginalForDispositionApproval(PhysicalFileContainer pendingFolder, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(pendingFolder, PhysicalFileKind.SuspensePendingDispositionApproval);
        RequireStatus("FIL-005", "send the original for disposition approval", PhysicalOriginalStatus.FiledActive);

        OriginalStatus = PhysicalOriginalStatus.SentForDispositionApproval;
        SuspenseCopyContainerId = pendingFolder.Id;
        Add(PhysicalDocumentEventKind.OriginalSentForDispositionApproval, userId, at, null, narrative);
        Add(PhysicalDocumentEventKind.SuspenseCopyRetained, userId, at, pendingFolder.Id, null);
    }

    /// <summary>
    /// AR 195-5 2-4h - after ALL items are disposed, the original and related documents go to
    /// the inactive file. Whether all items are disposed is the voucher's derived status; the
    /// caller checks it. The three-year clock starts at <paramref name="inactiveAtUtc"/>.
    /// </summary>
    public void FileOriginalInactive(PhysicalFileContainer inactiveFile, int userId, DateTimeOffset inactiveAtUtc, string? narrative = null)
    {
        RequireContainer(inactiveFile, PhysicalFileKind.Inactive4137File);
        RequireStatus("FIL-006", "file the original as inactive",
            PhysicalOriginalStatus.FiledActive, PhysicalOriginalStatus.SentForDispositionApproval, PhysicalOriginalStatus.NotYetFiled);

        OriginalStatus = PhysicalOriginalStatus.FiledInactive;
        OriginalContainerId = null;
        SuspenseCopyContainerId = null;
        InactiveContainerId = inactiveFile.Id;
        InactiveSinceUtc = AccountabilityTime.Normalize(inactiveAtUtc);
        Add(PhysicalDocumentEventKind.OriginalFiledInactive, userId, inactiveAtUtc, inactiveFile.Id, narrative);
    }

    /// <summary>
    /// AR 195-5 2-7g - on permanent transfer the original and duplicate go with the evidence to
    /// the gaining room, and the sending room retains a copy showing the disposition, filed
    /// inactive. This ends the sending room's accountability for the paper record; the
    /// investigation may well continue. The clock starts now.
    /// </summary>
    public void TransferOriginalToGainingRoom(PhysicalFileContainer inactiveFile, string gainingEvidenceRoom, int userId, DateTimeOffset at, string? narrative = null)
    {
        RequireContainer(inactiveFile, PhysicalFileKind.Inactive4137File);
        RequireStatus("FIL-007", "transfer the original to the gaining evidence room",
            PhysicalOriginalStatus.FiledActive, PhysicalOriginalStatus.NotYetFiled);
        var gaining = Guard.NotBlank(gainingEvidenceRoom, "FIL-007", "Gaining evidence room");

        OriginalStatus = PhysicalOriginalStatus.TransferredToGainingRoom;
        OriginalContainerId = null;
        SuspenseCopyContainerId = null;
        HoldsCopyOnly = true;
        CopyReason = CopyRetentionReason.OriginalTransferredToGainingRoom;
        InactiveContainerId = inactiveFile.Id;
        InactiveSinceUtc = AccountabilityTime.Normalize(at);
        Add(PhysicalDocumentEventKind.OriginalTransferredToGainingRoom, userId, at, null, $"To {gaining}. {narrative}".Trim());
        Add(PhysicalDocumentEventKind.SendingRoomCopyFiledInactive, userId, at, inactiveFile.Id, null);
    }

    /// <summary>
    /// AR 195-5 2-4g - a COPY is used as the suspense copy and placed in the inactive file, noting
    /// the disposition of the original, when the original is a permanent part of the record of
    /// trial (1), accompanies evidence released to an external agency (2), or is not available
    /// for other reasons (3). The clock starts now.
    /// </summary>
    public void FileCopyInactiveBecauseOriginalUnavailable(
        PhysicalFileContainer inactiveFile, CopyRetentionReason reason, string dispositionOfOriginal, int userId, DateTimeOffset at)
    {
        RequireContainer(inactiveFile, PhysicalFileKind.Inactive4137File);

        if (reason is not (CopyRetentionReason.OriginalInRecordOfTrial
            or CopyRetentionReason.OriginalWithExternalAgency
            or CopyRetentionReason.OriginalUnavailableOther))
        {
            throw new DomainRuleViolationException(
                "FIL-008", "State which of AR 195-5 para 2-4g(1), (2) or (3) applies.");
        }

        RequireStatus("FIL-008", "file a copy as inactive because the original is unavailable",
            PhysicalOriginalStatus.FiledActive, PhysicalOriginalStatus.AccompanyingTemporaryRelease,
            PhysicalOriginalStatus.SentForDispositionApproval, PhysicalOriginalStatus.NotYetFiled);

        var disposition = Guard.NotBlank(dispositionOfOriginal, "FIL-008", "Disposition of the original (AR 195-5 2-4g)");

        OriginalStatus = reason switch
        {
            CopyRetentionReason.OriginalInRecordOfTrial => PhysicalOriginalStatus.PartOfRecordOfTrial,
            CopyRetentionReason.OriginalWithExternalAgency => PhysicalOriginalStatus.WithExternalAgency,
            _ => PhysicalOriginalStatus.UnavailableOther
        };
        OriginalContainerId = null;
        SuspenseCopyContainerId = null;
        HoldsCopyOnly = true;
        CopyReason = reason;
        InactiveContainerId = inactiveFile.Id;
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
    /// The custodian confirms the inactive PAPER record was actually destroyed. Requires the
    /// record to be inactive and eligible at <paramref name="at"/>. Nothing here touches EMC's
    /// digital records, the scan, or the case-file copy (FIL-009, DEC-07).
    /// </summary>
    public void ConfirmDestruction(int userId, DateTimeOffset at, string narrative)
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

        DestructionConfirmedAtUtc = AccountabilityTime.Normalize(at);
        DestructionConfirmedByUserId = userId;
        OriginalStatus = PhysicalOriginalStatus.Destroyed;
        InactiveContainerId = null;
        Add(PhysicalDocumentEventKind.DestructionConfirmed, userId, at, null, what);
    }

    public void AddNote(int userId, DateTimeOffset at, string narrative)
        => Add(PhysicalDocumentEventKind.Note, userId, at, null, Guard.NotBlank(narrative, "FIL-004", "Note"));

    private void Add(PhysicalDocumentEventKind kind, int userId, DateTimeOffset at, int? containerId, string? narrative)
        => _events.Add(new PhysicalVoucherDocumentEvent(this, kind, OriginalStatus, userId, at, containerId, narrative));

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

    private void RequireStatus(string requirementId, string action, params PhysicalOriginalStatus[] allowed)
    {
        if (!allowed.Contains(OriginalStatus))
        {
            throw new DomainRuleViolationException(
                requirementId,
                $"Cannot {action}: the original DA Form 4137 is {OriginalStatus}.");
        }
    }
}

/// <summary>One thing that happened to the paper record. Append-only.</summary>
public class PhysicalVoucherDocumentEvent : Entity, IAppendOnly
{
    private PhysicalVoucherDocumentEvent() { }

    internal PhysicalVoucherDocumentEvent(
        PhysicalVoucherDocument document,
        PhysicalDocumentEventKind kind,
        PhysicalOriginalStatus resultingStatus,
        int recordedByUserId,
        DateTimeOffset occurredAtUtc,
        int? containerId,
        string? narrative)
    {
        DocumentId = document.Id;
        Document = document;
        Kind = kind;
        ResultingOriginalStatus = resultingStatus;
        RecordedByUserId = recordedByUserId;
        OccurredAtUtc = AccountabilityTime.Normalize(occurredAtUtc);
        ContainerId = containerId;
        Narrative = Guard.TrimToNull(narrative);
    }

    public int DocumentId { get; private set; }
    public PhysicalVoucherDocument? Document { get; private set; }
    public PhysicalDocumentEventKind Kind { get; private set; }
    public PhysicalOriginalStatus ResultingOriginalStatus { get; private set; }
    public int RecordedByUserId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public int? ContainerId { get; private set; }
    public string? Narrative { get; private set; }
}
