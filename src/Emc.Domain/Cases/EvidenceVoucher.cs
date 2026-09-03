using Emc.Domain.Common;

namespace Emc.Domain.Cases;

/// <summary>
/// One DA Form 4137 (Evidence/Property Custody Document).
///
/// AR 195-5 2-3a: "Regardless of how evidence is obtained, all physical evidence will be
/// inventoried and accounted for on DA Form 4137."
///
/// The voucher's status is DERIVED from its items and is never stored as maintained state.
/// AR 195-5 2-4h makes the voucher inactive only "after all items of evidence listed on a DA
/// Form 4137 have been properly disposed", and 2-5b(1)(d) contemplates different disposition
/// dates for different items on one form. A voucher-level status column cannot represent that
/// without losing information (VCH-007, invariant I-16).
/// </summary>
public class EvidenceVoucher : Entity, IConcurrencyStamped
{
    private readonly List<EvidenceItem> _items = [];
    private readonly List<OfficialDocumentNumberAssignment> _documentNumberAssignments = [];

    private EvidenceVoucher() { }

    public EvidenceVoucher(
        int caseId,
        int evidenceRoomId,
        TemporaryEvidenceIdentifier temporaryIdentifier,
        int preparedByUserId,
        string receivingActivity,
        string receivingActivityLocation,
        string receivedFrom,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset acquiredAtLocal,
        int createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(temporaryIdentifier);

        CaseId = caseId;
        EvidenceRoomId = evidenceRoomId;
        TemporaryIdentifier = temporaryIdentifier.ToString();

        // AR 195-5 2-3b: "The DALEO or Army CI agent who first acquired the evidence must
        // prepare the DA Form 4137."
        PreparedByUserId = preparedByUserId;

        ReceivingActivity = Guard.NotBlank(receivingActivity, "VCH-001", "Receiving activity");
        ReceivingActivityLocation = Guard.NotBlank(receivingActivityLocation, "VCH-001", "Location");
        ReceivedFrom = Guard.NotBlank(receivedFrom, "VCH-001", "Person or place from whom received");

        AcquiredAtUtc = acquiredAtUtc;
        AcquiredAtLocal = acquiredAtLocal;
        AcquiredAtOffset = acquiredAtLocal.Offset;

        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        IsSubmitted = false;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int CaseId { get; private set; }
    public Case? Case { get; private set; }

    public int EvidenceRoomId { get; private set; }

    /// <summary>EMC-generated placeholder until the custodian transcribes the official number (VCH-003).</summary>
    public string TemporaryIdentifier { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-3b — the agent who first acquired the evidence.</summary>
    public int PreparedByUserId { get; private set; }

    public string ReceivingActivity { get; private set; } = string.Empty;
    public string ReceivingActivityLocation { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-3b — the person or place from whom/where the evidence was obtained.</summary>
    public string ReceivedFrom { get; private set; } = string.Empty;

    /// <summary>
    /// AR 195-5 2-3b — when evidence is collected in response to a request for assistance (RFA),
    /// "both the seizing and requesting offices law enforcement report number will be recorded
    /// on the DA Form 4137 and DA Form 4002" (CASE-002).
    /// </summary>
    public string? RequestingOfficeCaseNumber { get; private set; }

    public bool IsRequestForAssistance { get; private set; }

    public DateTimeOffset AcquiredAtUtc { get; private set; }
    public DateTimeOffset AcquiredAtLocal { get; private set; }
    public TimeSpan AcquiredAtOffset { get; private set; }

    public int CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>AR 195-5 2-4a — submitted for custodian acceptance.</summary>
    public bool IsSubmitted { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public int? SubmittedByUserId { get; private set; }

    public string? Remarks { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyList<EvidenceItem> Items => _items.AsReadOnly();

    public IReadOnlyList<OfficialDocumentNumberAssignment> DocumentNumberAssignments
        => _documentNumberAssignments.AsReadOnly();

    /// <summary>
    /// The document number currently in force. AR 195-5 2-7g allows a voucher to carry more than
    /// one over its life: on permanent transfer between evidence rooms the receiving custodian
    /// assigns the receiving room's next number and the prior number is "lined through in such a
    /// way that it remains legible". Superseded assignments are retained and remain visible
    /// (VCH-008, invariant I-05).
    /// </summary>
    public OfficialDocumentNumberAssignment? CurrentDocumentNumberAssignment
        => _documentNumberAssignments
            .OrderByDescending(a => a.EnteredAtUtc)
            .ThenByDescending(a => a.Id)
            .FirstOrDefault();

    public bool HasOfficialDocumentNumber => CurrentDocumentNumberAssignment is not null;

    /// <summary>The number to show: the official one once assigned, otherwise the temporary identifier.</summary>
    public string DisplayIdentifier
        => CurrentDocumentNumberAssignment?.DocumentNumber ?? TemporaryIdentifier;

    /// <summary>
    /// Derived voucher status (VCH-007). AR 195-5 2-4h: the voucher becomes inactive only after
    /// ALL its items have been properly disposed.
    /// </summary>
    public VoucherDerivedStatus DerivedStatus
    {
        get
        {
            if (_items.Count == 0)
            {
                return VoucherDerivedStatus.Draft;
            }

            if (!IsSubmitted)
            {
                return VoucherDerivedStatus.Draft;
            }

            var terminalCount = _items.Count(i => IsTerminal(i.AccountabilityStatus));
            if (terminalCount == _items.Count)
            {
                return VoucherDerivedStatus.Inactive;
            }

            var acceptedCount = _items.Count(i => i.AccountabilityStatus >= AccountabilityStatus.InEvidenceRoom);
            if (acceptedCount == 0)
            {
                return VoucherDerivedStatus.AwaitingCustodianAcceptance;
            }

            return acceptedCount < _items.Count
                ? VoucherDerivedStatus.PartiallyAccepted
                : VoucherDerivedStatus.Active;
        }
    }

    public static bool IsTerminal(AccountabilityStatus status)
        => status is AccountabilityStatus.Disposed
            or AccountabilityStatus.ReliefGranted
            or AccountabilityStatus.PermanentlyTransferred;

    /// <summary>
    /// AR 195-5 2-3g contemplates the custodian having the submitting agent "correct and initial
    /// all errors" before acceptance. Item composition is therefore mutable only while the
    /// voucher is a draft; afterwards, change happens through a CorrectionEvent
    /// (VCH-010, invariant I-10).
    /// </summary>
    public bool AllowsItemEditing => !IsSubmitted;

    public void MarkAsRequestForAssistance(string requestingOfficeCaseNumber)
    {
        RequestingOfficeCaseNumber = Guard.NotBlank(
            requestingOfficeCaseNumber, "CASE-002", "Requesting office case control number");
        IsRequestForAssistance = true;
    }

    public void UpdateHeader(
        string receivingActivity,
        string receivingActivityLocation,
        string receivedFrom,
        string? remarks)
    {
        RequireDraft("VCH-010", "voucher header");

        ReceivingActivity = Guard.NotBlank(receivingActivity, "VCH-001", "Receiving activity");
        ReceivingActivityLocation = Guard.NotBlank(receivingActivityLocation, "VCH-001", "Location");
        ReceivedFrom = Guard.NotBlank(receivedFrom, "VCH-001", "Person or place from whom received");
        Remarks = Guard.TrimToNull(remarks);
    }

    /// <summary>
    /// Adds an item. Item numbers are contiguous from 1 within the voucher (AR 195-5 2-3d,
    /// invariant I-01).
    /// </summary>
    public EvidenceItem AddItem(
        string description,
        string? quantity,
        string? serialNumber,
        string? uniqueDeviceIdentifier,
        bool isPossibleBiohazard,
        bool isFungible,
        bool isSealed,
        string? sealDescription)
    {
        RequireDraft("VCH-010", "items");

        var item = new EvidenceItem(
            voucher: this,
            itemNumber: _items.Count + 1,
            description: description,
            quantity: quantity,
            serialNumber: serialNumber,
            uniqueDeviceIdentifier: uniqueDeviceIdentifier,
            isPossibleBiohazard: isPossibleBiohazard,
            isFungible: isFungible,
            isSealed: isSealed,
            sealDescription: sealDescription);

        _items.Add(item);
        return item;
    }

    /// <summary>Removes an item and renumbers the remainder so numbering stays contiguous (I-01).</summary>
    public void RemoveItem(EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireDraft("VCH-010", "items");

        if (!_items.Remove(item))
        {
            throw new DomainRuleViolationException("ITEM-002", "The item does not belong to this voucher.");
        }

        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].Renumber(i + 1);
        }
    }

    /// <summary>
    /// AR 195-5 2-4a — submit for custodian acceptance. A voucher must contain at least one item
    /// (2-3a, VCH-011, invariant I-02).
    /// </summary>
    public void SubmitForCustodianIntake(int submittedByUserId, DateTimeOffset submittedAtUtc)
    {
        if (IsSubmitted)
        {
            throw new DomainRuleViolationException(
                "VCH-010", "This voucher has already been submitted for custodian intake.");
        }

        if (_items.Count == 0)
        {
            throw new DomainRuleViolationException(
                "VCH-011",
                "AR 195-5 2-3a: a DA Form 4137 must account for at least one item of evidence "
                + "before it can be submitted.");
        }

        IsSubmitted = true;
        SubmittedByUserId = submittedByUserId;
        SubmittedAtUtc = submittedAtUtc;
    }

    /// <summary>
    /// Records the official document number the custodian assigned in the authoritative evidence
    /// ledger (AR 195-5 2-4c). EMC transcribes; it does not assign (EMC-002).
    /// </summary>
    public OfficialDocumentNumberAssignment RecordOfficialDocumentNumber(
        EvidenceDocumentNumber documentNumber,
        int enteredByUserId,
        DateTimeOffset enteredAtUtc,
        bool attestedAssignedInAuthoritativeLedger,
        string? supersessionReason = null)
    {
        ArgumentNullException.ThrowIfNull(documentNumber);

        if (!IsSubmitted)
        {
            throw new DomainRuleViolationException(
                "VCH-006",
                "AR 195-5 2-4c: the document number is assigned upon receipt of the evidence and "
                + "the DA Form 4137 by the evidence custodian. The voucher has not been submitted.");
        }

        var current = CurrentDocumentNumberAssignment;
        if (current is not null && string.IsNullOrWhiteSpace(supersessionReason))
        {
            throw new DomainRuleViolationException(
                "VCH-008",
                "AR 195-5 2-7g: this voucher already carries document number "
                + $"{current.DocumentNumber}. Replacing it requires a supersession reason, and the "
                + "prior number remains visible.");
        }

        var assignment = new OfficialDocumentNumberAssignment(
            voucherId: Id,
            evidenceRoomId: EvidenceRoomId,
            documentNumber: documentNumber,
            enteredByUserId: enteredByUserId,
            enteredAtUtc: enteredAtUtc,
            attestedAssignedInAuthoritativeLedger: attestedAssignedInAuthoritativeLedger,

            // AR 195-5 2-7g - the NEW assignment names the one it replaces. The prior row is
            // never modified, so the assignment history is strictly insert-only.
            supersedes: current,
            supersessionReason: supersessionReason);

        _documentNumberAssignments.Add(assignment);
        return assignment;
    }

    private void RequireDraft(string requirementId, string what)
    {
        if (!AllowsItemEditing)
        {
            throw new DomainRuleViolationException(
                requirementId,
                $"AR 195-5 2-3g: {what} may only be changed while the voucher is a draft. Once the "
                + "voucher has been submitted for custodian intake, accountability data is "
                + "append-only and must be changed through a correction.");
        }
    }
}
