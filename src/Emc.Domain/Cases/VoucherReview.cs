using Emc.Domain.Common;

namespace Emc.Domain.Cases;

/// <summary>
/// Where a DA Form 4137 stands in the custodian's pre-acceptance review.
///
/// AR 195-5 2-3g: "Evidence custodians will review the DA Form 4137 submitted with evidence and
/// have the submitting DALEO or Army CI agent correct and initial all errors." That is a
/// workflow between two people about a FORM, before the evidence is received under 2-4c. It is
/// not the 1-7c(3) path, which governs a custodian finding an incorrect entry in an accepted
/// accountability record; conflating the two would demand a custodian-error MFR for an agent
/// fixing a typo on a form the custodian has not yet accepted.
///
/// Separate from the item's <see cref="AccountabilityStatus"/>, which tracks each ITEM's
/// accountability, because the review is of the voucher as a whole: the custodian returns the
/// form, not item 3.
/// </summary>
public enum VoucherReviewStage
{
    Draft = 0,

    /// <summary>AR 195-5 2-4a - submitted; the custodian is reviewing the form under 2-3g.</summary>
    SubmittedForCustodianReview = 1,

    /// <summary>AR 195-5 2-3g - the custodian identified errors and returned the form to the agent.</summary>
    ReturnedToSubmittingAgentForCorrection = 2,

    /// <summary>AR 195-5 2-3g - the submitting agent corrected and initialed the paper form.</summary>
    CorrectedBySubmittingAgent = 3,

    /// <summary>The corrected form is before the custodian again.</summary>
    ResubmittedForCustodianReview = 4,

    /// <summary>AR 195-5 2-4c - the custodian received the evidence and assigned the document number.</summary>
    AcceptedByCustodian = 5
}

public enum VoucherReviewActionKind
{
    Submitted = 1,
    ReturnedForCorrection = 2,
    CorrectedBySubmittingAgent = 3,
    Resubmitted = 4,
    Accepted = 5,

    /// <summary>AR 195-5 2-3g - the submitting agent withdrew a line entered in error from the returned form.</summary>
    LineWithdrawn = 6
}

/// <summary>
/// One step of the 2-3g review, recorded as it happened: who did it, when, and what they said.
///
/// Append-only. The review record is the answer to "why did this voucher take four days to
/// accept" and "what did the custodian find wrong", and it must be as honest as the rest of the
/// accountability record.
///
/// What this does NOT record: the agent's initials. 2-3g has the agent "correct and initial" the
/// errors ON THE FORM. EMC is a companion (2-5c); it records the agent's attestation that the
/// paper form was corrected and initialed, in the same way it records a ledger attestation
/// (AUD-013). It supplies no initials and no signature of its own.
/// </summary>
public class VoucherReviewAction : Entity, IAppendOnly
{
    private VoucherReviewAction() { }

    internal VoucherReviewAction(
        EvidenceVoucher voucher,
        VoucherReviewActionKind kind,
        VoucherReviewStage resultingStage,
        int actorUserId,
        DateTimeOffset occurredAtUtc,
        string? narrative,
        bool? paperFormCorrectedAndInitialedAttested)
    {
        ArgumentNullException.ThrowIfNull(voucher);

        VoucherId = voucher.Id;
        Voucher = voucher;
        Kind = kind;
        ResultingStage = resultingStage;
        ActorUserId = actorUserId;
        OccurredAtUtc = AccountabilityTime.Normalize(occurredAtUtc);
        Narrative = Guard.TrimToNull(narrative);
        PaperFormCorrectedAndInitialedAttested = paperFormCorrectedAndInitialedAttested;
    }

    public int VoucherId { get; private set; }
    public EvidenceVoucher? Voucher { get; private set; }

    public VoucherReviewActionKind Kind { get; private set; }

    /// <summary>The stage the voucher was in after this action.</summary>
    public VoucherReviewStage ResultingStage { get; private set; }

    /// <summary>The custodian who returned it, the agent who corrected or resubmitted it, and so on.</summary>
    public int ActorUserId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>
    /// For a return: what the custodian identified (2-3g). For a correction: what the agent
    /// corrected. For acceptance: the document number assigned. Free text, as an MFR would be.
    /// </summary>
    public string? Narrative { get; private set; }

    /// <summary>
    /// For a correction: the agent's attestation that the PAPER DA Form 4137 was corrected and
    /// initialed as 2-3g requires. An attestation, not an initial (AUD-013). Null for other kinds.
    /// </summary>
    public bool? PaperFormCorrectedAndInitialedAttested { get; private set; }
}
