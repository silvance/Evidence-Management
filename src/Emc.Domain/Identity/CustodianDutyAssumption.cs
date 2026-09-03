using Emc.Domain.Common;

namespace Emc.Domain.Identity;

/// <summary>
/// A period during which the ALTERNATE evidence custodian actually assumes the primary
/// custodian's duties.
///
/// This is a regulatory correction. AR 195-5 distinguishes two things the earlier model
/// conflated:
///
///   (a) APPOINTMENT as primary or alternate custodian (1-4g(1), 1-7b). An alternate may hold
///       that appointment for months or years.
///
///   (b) The alternate ASSUMING the primary's duties during the primary's temporary absence
///       (1-4i). This is a distinct, much shorter period.
///
/// 1-4i: "The alternate evidence custodian will assume the duties and responsibilities of the
/// primary evidence custodian during his or her temporary absence. A temporary absence is more
/// than 1 working day and not more than 30 consecutive days."
///
/// Two consequences the earlier model got wrong:
///
///   1. Holding the alternate appointment did NOT, by itself, authorize acting as the evidence
///      custodian. Authority requires an open duty-assumption period (IAM-006).
///   2. The 30-consecutive-day limit runs from the date the alternate ASSUMED DUTIES, not from
///      the date of their appointment. Measuring from the appointment date produced a limit that
///      expired while the alternate had never acted at all.
///
/// 1-7c(1) and 1-7c(2) require handwritten, signed statements in the evidence ledger when the
/// alternate assumes duties and when the primary resumes them. EMC records that those paper
/// entries were made; it does not produce them (AUD-013).
///
/// Requirements: IAM-006, IAM-019, IAM-020.
/// </summary>
public class CustodianDutyAssumption : Entity, IConcurrencyStamped
{
    /// <summary>
    /// AR 195-5 1-4i - "A temporary absence is more than 1 working day and not more than 30
    /// consecutive days."
    ///
    /// This bounds the PRIMARY CUSTODIAN'S ABSENCE, not the period the alternate happened to be
    /// acting. An exact <see cref="TimeSpan"/> rather than a day count, so that 30 days and one
    /// second is over the limit - truncating to whole days would have allowed very nearly 31.
    /// </summary>
    public static readonly TimeSpan MaximumTemporaryAbsence = TimeSpan.FromDays(30);

    private CustodianDutyAssumption() { }

    public CustodianDutyAssumption(
        int evidenceRoomId,
        int primaryAppointmentId,
        int alternateAppointmentId,
        int alternateUserId,
        DateTimeOffset primaryAbsenceStart,
        DateTimeOffset alternateAssumedDutiesAt,
        string assumptionLedgerAttestation,
        int recordedByUserId,
        DateTimeOffset recordedAtUtc,
        string? reasonForAbsence = null,
        DateTimeOffset? expectedAbsenceEnd = null)
    {
        if (alternateAssumedDutiesAt < primaryAbsenceStart)
        {
            throw new DomainRuleViolationException(
                "IAM-019",
                "The alternate cannot assume duties before the primary custodian's absence begins.");
        }

        // IAM-021. AR 195-5 3-2d: "if it is known that the primary custodian will be gone for more
        // than 30 consecutive calendar days, the alternate will be appointed on orders as the
        // primary custodian, and a joint inventory will be conducted."
        //
        // An absence already known to exceed the limit is therefore NOT an ordinary temporary
        // assumption, and recording it as one would misrepresent the regulation. It takes the
        // PrimaryCustodianTransition path instead.
        if (expectedAbsenceEnd is not null
            && expectedAbsenceEnd.Value - primaryAbsenceStart > MaximumTemporaryAbsence)
        {
            throw new DomainRuleViolationException(
                "IAM-021",
                "AR 195-5 para 3-2d: this absence is already known to exceed 30 consecutive days, "
                + "so it cannot be recorded as a temporary assumption of duties by the alternate. "
                + "The alternate is appointed on orders as the primary custodian and a joint "
                + "inventory is conducted.");
        }

        // The alternate cannot begin acting after the absence has already outrun the limit - there
        // would be no temporary-absence authority left to assume.
        if (alternateAssumedDutiesAt - primaryAbsenceStart > MaximumTemporaryAbsence)
        {
            throw new DomainRuleViolationException(
                "IAM-021",
                "AR 195-5 paras 1-4i and 3-2d: the primary custodian's absence has already "
                + "exceeded 30 consecutive days, so there is no temporary-absence authority for "
                + "the alternate to assume. A primary appointment on orders and a joint inventory "
                + "are required.");
        }

        EvidenceRoomId = evidenceRoomId;
        PrimaryAppointmentId = primaryAppointmentId;
        AlternateAppointmentId = alternateAppointmentId;
        AlternateUserId = alternateUserId;
        PrimaryAbsenceStart = primaryAbsenceStart;
        AlternateAssumedDutiesAt = alternateAssumedDutiesAt;

        // AR 195-5 1-7c(1) requires the alternate to enter and SIGN the prescribed statement in
        // the evidence ledger. EMC records that this was done on paper (AUD-013).
        AssumptionLedgerAttestation = Guard.NotBlank(
            assumptionLedgerAttestation, "IAM-019", "Ledger assumption attestation");

        ReasonForAbsence = Guard.TrimToNull(reasonForAbsence);
        ExpectedAbsenceEnd = expectedAbsenceEnd;
        RecordedByUserId = recordedByUserId;
        RecordedAtUtc = recordedAtUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int EvidenceRoomId { get; private set; }

    public int PrimaryAppointmentId { get; private set; }
    public int AlternateAppointmentId { get; private set; }

    /// <summary>Denormalized so authorization can filter without joining the appointment.</summary>
    public int AlternateUserId { get; private set; }

    /// <summary>
    /// AR 195-5 1-4i - when the primary's temporary absence began.
    ///
    /// THIS is what the 30-day limit measures. An earlier version measured from
    /// <see cref="AlternateAssumedDutiesAt"/>, which meant a primary could be absent for 100 days
    /// and the alternate could then start a fresh 30-day window - a period of nearly five months
    /// covered by a provision the regulation caps at 30 days.
    /// </summary>
    public DateTimeOffset PrimaryAbsenceStart { get; private set; }

    /// <summary>
    /// When the absence is expected to end, where known at the outset. AR 195-5 3-2d routes a
    /// known long absence to a primary appointment plus joint inventory instead.
    /// </summary>
    public DateTimeOffset? ExpectedAbsenceEnd { get; private set; }

    /// <summary>
    /// AR 195-5 1-7c(1) - when the alternate actually assumed duties and signed the ledger.
    ///
    /// This gates ACTING AUTHORITY: the alternate has none before it. It does NOT start the
    /// regulatory clock - see <see cref="PrimaryAbsenceStart"/>. The two are separate concepts:
    /// how long the primary has been absent, and how long the alternate has been acting.
    /// </summary>
    public DateTimeOffset AlternateAssumedDutiesAt { get; private set; }

    /// <summary>AR 195-5 1-7c(1) - the ledger entry the alternate wrote and signed.</summary>
    public string AssumptionLedgerAttestation { get; private set; } = string.Empty;

    /// <summary>AR 195-5 1-7c(2) - when the primary resumed. Null while the alternate still acts.</summary>
    public DateTimeOffset? PrimaryResumedAt { get; private set; }

    /// <summary>AR 195-5 1-7c(2) - the primary's signed resumption entry in the ledger.</summary>
    public string? ResumptionLedgerAttestation { get; private set; }

    public string? ReasonForAbsence { get; private set; }

    public int RecordedByUserId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public int? ResumptionRecordedByUserId { get; private set; }
    public DateTimeOffset? ResumptionRecordedAtUtc { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>True while the alternate holds the primary's duties at <paramref name="at"/>.</summary>
    public bool IsActiveAt(DateTimeOffset at)
        => AlternateAssumedDutiesAt <= at && (PrimaryResumedAt is null || PrimaryResumedAt > at);

    /// <summary>
    /// How long the PRIMARY has been absent (AR 195-5 1-4i). Exact, not truncated to whole days.
    /// </summary>
    public TimeSpan AbsenceDurationAt(DateTimeOffset at)
        => at < PrimaryAbsenceStart ? TimeSpan.Zero : at - PrimaryAbsenceStart;

    /// <summary>
    /// How long the ALTERNATE has actually been acting. Informational: useful on screen and at
    /// inspection, but not what AR 195-5 1-4i bounds.
    /// </summary>
    public TimeSpan ActingDurationAt(DateTimeOffset at)
        => at < AlternateAssumedDutiesAt ? TimeSpan.Zero : at - AlternateAssumedDutiesAt;

    /// <summary>
    /// True once the primary's absence has run beyond the 30 consecutive days AR 195-5 1-4i
    /// permits. Para 3-2d then requires the alternate to be appointed primary ON ORDERS and a
    /// joint inventory conducted - so past this point the alternate has no temporary-absence
    /// authority left, and EMC denies rather than warns (IAM-020).
    /// </summary>
    public bool ExceedsTemporaryAbsenceLimitAt(DateTimeOffset at)
        => IsActiveAt(at) && AbsenceDurationAt(at) > MaximumTemporaryAbsence;

    /// <summary>
    /// How long remains of the regulatory window. Negative once exceeded. Drives the advance
    /// advisory, whose threshold is LOCAL/DESIGN - AR 195-5 states no warning point.
    /// </summary>
    public TimeSpan RemainingTemporaryAbsenceAt(DateTimeOffset at)
        => MaximumTemporaryAbsence - AbsenceDurationAt(at);

    /// <summary>
    /// AR 195-5 1-7c(2) - the primary resumes and signs the prescribed ledger statement. The
    /// alternate's authority to act as evidence custodian ends here.
    /// </summary>
    public void RecordPrimaryResumption(
        DateTimeOffset primaryResumedAt,
        string resumptionLedgerAttestation,
        int recordedByUserId,
        DateTimeOffset recordedAtUtc)
    {
        if (PrimaryResumedAt is not null)
        {
            throw new DomainRuleViolationException(
                "IAM-019", "The primary custodian has already resumed duties for this period.");
        }

        if (primaryResumedAt < AlternateAssumedDutiesAt)
        {
            throw new DomainRuleViolationException(
                "IAM-019",
                "The primary cannot resume duties before the alternate assumed them.");
        }

        PrimaryResumedAt = primaryResumedAt;
        ResumptionLedgerAttestation = Guard.NotBlank(
            resumptionLedgerAttestation, "IAM-019", "Ledger resumption attestation");
        ResumptionRecordedByUserId = recordedByUserId;
        ResumptionRecordedAtUtc = recordedAtUtc;
    }

    /// <summary>
    /// AR 195-5 1-7c(2) - "If the absence is 30 calendar days or less, there is no requirement to
    /// conduct a 100 percent inventory." Beyond that, 3-2d requires a joint inventory.
    /// </summary>
    public bool RequiresHundredPercentInventoryOnResumption
        => PrimaryResumedAt is not null
           && PrimaryResumedAt.Value - PrimaryAbsenceStart > MaximumTemporaryAbsence;
}
