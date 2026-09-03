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
    /// AR 195-5 1-4i - "not more than 30 consecutive days". Beyond this, 3-2d requires the
    /// alternate to be appointed primary on orders and a joint inventory conducted.
    /// </summary>
    public const int MaximumConsecutiveDays = 30;

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
        string? reasonForAbsence = null)
    {
        if (alternateAssumedDutiesAt < primaryAbsenceStart)
        {
            throw new DomainRuleViolationException(
                "IAM-019",
                "The alternate cannot assume duties before the primary custodian's absence begins.");
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
        RecordedByUserId = recordedByUserId;
        RecordedAtUtc = recordedAtUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int EvidenceRoomId { get; private set; }

    public int PrimaryAppointmentId { get; private set; }
    public int AlternateAppointmentId { get; private set; }

    /// <summary>Denormalized so authorization can filter without joining the appointment.</summary>
    public int AlternateUserId { get; private set; }

    /// <summary>AR 195-5 1-4i - when the primary's temporary absence began.</summary>
    public DateTimeOffset PrimaryAbsenceStart { get; private set; }

    /// <summary>
    /// AR 195-5 1-7c(1) - when the alternate assumed duties. THIS is the date the 30-consecutive-day
    /// limit runs from, not the appointment date.
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
    /// Consecutive days since the alternate ASSUMED DUTIES (AR 195-5 1-4i). The earlier model
    /// measured this from the appointment date, which was wrong.
    /// </summary>
    public int ConsecutiveDaysAt(DateTimeOffset at)
        => at < AlternateAssumedDutiesAt ? 0 : (int)(at - AlternateAssumedDutiesAt).TotalDays;

    /// <summary>
    /// True once the assumption has run beyond the 30 consecutive days AR 195-5 1-4i permits for
    /// a temporary absence. Para 3-2d then requires the alternate to be appointed primary on
    /// orders and a joint inventory conducted.
    /// </summary>
    public bool ExceedsTemporaryAbsenceLimitAt(DateTimeOffset at)
        => IsActiveAt(at) && ConsecutiveDaysAt(at) > MaximumConsecutiveDays;

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
           && (PrimaryResumedAt.Value - AlternateAssumedDutiesAt).TotalDays > MaximumConsecutiveDays;
}
