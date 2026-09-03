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
        ReviewStage = VoucherReviewStage.Draft;
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

    /// <summary>
    /// Where the form stands in the custodian's pre-acceptance review (AR 195-5 2-3g). The only
    /// stored workflow state on the voucher; everything else about its status is derived.
    /// </summary>
    public VoucherReviewStage ReviewStage { get; private set; }

    /// <summary>
    /// AR 195-5 2-4a — currently before the custodian, or accepted. False while a draft, and
    /// false again while the custodian has returned the form for correction. Derived from
    /// <see cref="ReviewStage"/>; not stored, so it cannot drift from it.
    /// </summary>
    public bool IsSubmitted
        => ReviewStage is VoucherReviewStage.SubmittedForCustodianReview
            or VoucherReviewStage.ResubmittedForCustodianReview
            or VoucherReviewStage.AcceptedByCustodian;

    /// <summary>The most recent submission or resubmission.</summary>
    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    /// <summary>
    /// AR 195-5 2-3g names "the submitting DALEO or Army CI agent" as the person who corrects
    /// the form. This is that person; a correction recorded by anyone else is refused.
    /// </summary>
    public int? SubmittedByUserId { get; private set; }

    private readonly List<VoucherReviewAction> _reviewActions = [];

    /// <summary>The 2-3g review as it happened, in order. Append-only.</summary>
    public IReadOnlyList<VoucherReviewAction> ReviewActions => _reviewActions.AsReadOnly();

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

            if (ReviewStage == VoucherReviewStage.Draft)
            {
                return VoucherDerivedStatus.Draft;
            }

            // AR 195-5 2-3g - back with the submitting agent, or corrected but not yet
            // resubmitted. Not a draft: it has been submitted once and its review is on record.
            if (ReviewStage is VoucherReviewStage.ReturnedToSubmittingAgentForCorrection
                or VoucherReviewStage.CorrectedBySubmittingAgent)
            {
                return VoucherDerivedStatus.ReturnedForCorrection;
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
    public bool AllowsItemEditing
        => ReviewStage is VoucherReviewStage.Draft
            or VoucherReviewStage.ReturnedToSubmittingAgentForCorrection;

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

        // VCH-021. An item on a voucher the custodian returned (2-3g) already carries its
        // submission and return on its own history, and that history is append-only. It is
        // corrected, not deleted - a line through the entry on the paper form (2-5b(5)), not a
        // torn-out page. A never-submitted draft item has no history and may still be removed.
        if (item.Events.Count > 0)
        {
            throw new DomainRuleViolationException(
                "VCH-021",
                $"Item {item.ItemNumber} already carries accountability events and cannot be "
                + "removed. Correct its description instead; if it was listed in error, say so in "
                + "the description and in the correction recorded for the custodian (AR 195-5 2-3g).");
        }

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
        if (ReviewStage != VoucherReviewStage.Draft)
        {
            throw new DomainRuleViolationException(
                "VCH-010",
                ReviewStage is VoucherReviewStage.ReturnedToSubmittingAgentForCorrection
                    or VoucherReviewStage.CorrectedBySubmittingAgent
                    ? "This voucher was returned by the evidence custodian for correction "
                      + "(AR 195-5 2-3g). Record the correction, then resubmit it."
                    : "This voucher has already been submitted for custodian intake.");
        }

        RequireAtLeastOneItem();

        ReviewStage = VoucherReviewStage.SubmittedForCustodianReview;
        SubmittedByUserId = submittedByUserId;
        SubmittedAtUtc = submittedAtUtc;

        _reviewActions.Add(new VoucherReviewAction(
            this, VoucherReviewActionKind.Submitted, ReviewStage, submittedByUserId,
            submittedAtUtc, narrative: null, paperFormCorrectedAndInitialedAttested: null));
    }

    /// <summary>
    /// AR 195-5 2-3g — the custodian reviews the submitted form and has the submitting agent
    /// correct and initial all errors. Returning it records WHAT the custodian identified, so
    /// the agent knows what to fix and the record shows why acceptance waited.
    ///
    /// Possible only before acceptance. Once the custodian has received the evidence and assigned
    /// a document number (2-4c) the form is part of the accountability record, and an error in it
    /// is a 1-7c(3) matter for the custodian, not a return to the agent.
    /// </summary>
    public void ReturnForCorrection(int custodianUserId, string errorsIdentified, DateTimeOffset returnedAtUtc)
    {
        if (HasOfficialDocumentNumber || ReviewStage == VoucherReviewStage.AcceptedByCustodian)
        {
            throw new DomainRuleViolationException(
                "VCH-017",
                "AR 195-5 2-3g applies to a DA Form 4137 under review before acceptance. This "
                + "voucher has been received and assigned a document number (2-4c); an incorrect "
                + "entry in it is corrected by the custodian under para 1-7c(3), not returned to "
                + "the agent.");
        }

        if (ReviewStage is not (VoucherReviewStage.SubmittedForCustodianReview
            or VoucherReviewStage.ResubmittedForCustodianReview))
        {
            throw new DomainRuleViolationException(
                "VCH-017",
                $"A voucher can be returned for correction only while it is before the evidence "
                + $"custodian for review. This voucher is {ReviewStage}.");
        }

        var errors = Guard.NotBlank(
            errorsIdentified, "VCH-017", "The errors the custodian identified (AR 195-5 2-3g)");

        ReviewStage = VoucherReviewStage.ReturnedToSubmittingAgentForCorrection;

        _reviewActions.Add(new VoucherReviewAction(
            this, VoucherReviewActionKind.ReturnedForCorrection, ReviewStage, custodianUserId,
            returnedAtUtc, errors, paperFormCorrectedAndInitialedAttested: null));
    }

    /// <summary>
    /// AR 195-5 2-3g — the SUBMITTING agent corrects and initials the errors. Recorded by that
    /// agent and no one else, with what was corrected and the agent's attestation that the paper
    /// form was corrected and initialed. EMC supplies no initials of its own (AUD-013).
    /// </summary>
    public void RecordCorrectionBySubmittingAgent(
        int agentUserId,
        string whatWasCorrected,
        bool paperFormCorrectedAndInitialedAttested,
        DateTimeOffset correctedAtUtc)
    {
        if (ReviewStage != VoucherReviewStage.ReturnedToSubmittingAgentForCorrection)
        {
            throw new DomainRuleViolationException(
                "VCH-018",
                $"A correction under AR 195-5 2-3g is recorded against a voucher the custodian has "
                + $"returned. This voucher is {ReviewStage}.");
        }

        RequireSubmittingAgent(agentUserId, "VCH-018", "correct");

        var corrected = Guard.NotBlank(whatWasCorrected, "VCH-018", "What was corrected");

        if (!paperFormCorrectedAndInitialedAttested)
        {
            throw new DomainRuleViolationException(
                "VCH-019",
                "AR 195-5 2-3g has the submitting agent correct AND INITIAL all errors on the "
                + "DA Form 4137. Confirm that the paper form has been corrected and initialed. "
                + "This application records that attestation; it does not supply the initials.");
        }

        ReviewStage = VoucherReviewStage.CorrectedBySubmittingAgent;

        _reviewActions.Add(new VoucherReviewAction(
            this, VoucherReviewActionKind.CorrectedBySubmittingAgent, ReviewStage, agentUserId,
            correctedAtUtc, corrected, paperFormCorrectedAndInitialedAttested: true));
    }

    /// <summary>Puts the corrected form before the custodian again.</summary>
    public void Resubmit(int agentUserId, DateTimeOffset resubmittedAtUtc)
    {
        if (ReviewStage != VoucherReviewStage.CorrectedBySubmittingAgent)
        {
            throw new DomainRuleViolationException(
                "VCH-020",
                ReviewStage == VoucherReviewStage.ReturnedToSubmittingAgentForCorrection
                    ? "Record the correction (AR 195-5 2-3g) before resubmitting the voucher."
                    : $"Only a corrected voucher can be resubmitted. This voucher is {ReviewStage}.");
        }

        RequireSubmittingAgent(agentUserId, "VCH-020", "resubmit");
        RequireAtLeastOneItem();

        ReviewStage = VoucherReviewStage.ResubmittedForCustodianReview;
        SubmittedAtUtc = resubmittedAtUtc;

        _reviewActions.Add(new VoucherReviewAction(
            this, VoucherReviewActionKind.Resubmitted, ReviewStage, agentUserId,
            resubmittedAtUtc, narrative: null, paperFormCorrectedAndInitialedAttested: null));
    }

    private void RequireSubmittingAgent(int userId, string requirementId, string verb)
    {
        if (SubmittedByUserId != userId)
        {
            throw new DomainRuleViolationException(
                requirementId,
                $"AR 195-5 2-3g: the SUBMITTING agent corrects and initials the errors. Only the "
                + $"agent who submitted this voucher may {verb} it.");
        }
    }

    private void RequireAtLeastOneItem()
    {
        if (_items.Count == 0)
        {
            throw new DomainRuleViolationException(
                "VCH-011",
                "AR 195-5 2-3a: a DA Form 4137 must account for at least one item of evidence "
                + "before it can be submitted.");
        }
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
                ReviewStage == VoucherReviewStage.Draft
                    ? "AR 195-5 2-4c: the document number is assigned upon receipt of the evidence "
                      + "and the DA Form 4137 by the evidence custodian. The voucher has not been "
                      + "submitted."
                    : "AR 195-5 2-3g: this voucher has been returned to the submitting agent for "
                      + "correction and is not before the custodian. The agent records the "
                      + "correction and resubmits it; only then can it be received and numbered "
                      + "(2-4c).");
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

        // The first number is the custodian's acceptance of the form (2-4c) and closes the 2-3g
        // review. A later number (2-7g permanent transfer) changes nothing about the review.
        if (ReviewStage != VoucherReviewStage.AcceptedByCustodian)
        {
            ReviewStage = VoucherReviewStage.AcceptedByCustodian;

            _reviewActions.Add(new VoucherReviewAction(
                this, VoucherReviewActionKind.Accepted, ReviewStage, enteredByUserId,
                enteredAtUtc, $"Received and assigned evidence document number {assignment.DocumentNumber}.",
                paperFormCorrectedAndInitialedAttested: null));
        }

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
