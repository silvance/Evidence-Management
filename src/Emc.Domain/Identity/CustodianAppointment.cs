using Emc.Domain.Common;

namespace Emc.Domain.Identity;

/// <summary>
/// A written evidence-custodian appointment.
///
/// AR 195-5 does not vest custodial authority in a role. It vests it in a person named in a
/// written appointment:
///
///   1-4g(1)  CI unit commanders "appoint, in writing, ONE primary and ONE alternate evidence
///            custodian" — exactly one of each at a time (invariant I-06).
///   1-4i     The alternate assumes the primary's duties during a temporary absence, defined as
///            "more than 1 working day and not more than 30 consecutive days". In an emergency
///            another alternate may be appointed in writing, and "the appointment orders will
///            supersede the previous alternate evidence custodian's orders".
///   1-7a(1)(c) A CI evidence custodian must be a credentialed CI agent; CI agents in a
///            probationary program will not be appointed.
///   1-7b     A copy of the appointment documents is kept in the evidence room files per
///            AR 25-400-2, maintained as long as the position is held. AR 195-5 is cited as the
///            appointment authority.
///
/// This entity is why "may this person accept evidence today?" is answerable. A role flag alone
/// cannot express the one-primary-one-alternate rule or the alternate's time-bounded window.
///
/// Requirements: IAM-004, IAM-005, IAM-006, IAM-007, IAM-008.
/// </summary>
public class CustodianAppointment : Entity, IConcurrencyStamped
{
    private CustodianAppointment() { }

    public CustodianAppointment(
        int evidenceRoomId,
        int userId,
        CustodianAppointmentType appointmentType,
        PersonnelCategory personnelCategory,
        DateTimeOffset effectiveFrom,
        string appointmentOrderReference,
        string appointingAuthority,
        bool eligibilityAttested,
        int recordedByUserId,
        DateTimeOffset recordedAtUtc)
    {
        // AR 195-5 1-7a. The applicable eligibility rule depends on the appointee's personnel
        // category, and the two CI rules are genuinely different. EMC cannot verify either, so
        // the recording user attests to the correct one and the attestation is retained with the
        // category that determined it (IAM-008).
        if (!eligibilityAttested)
        {
            throw new DomainRuleViolationException(
                "IAM-008",
                $"AR 195-5 {EligibilityBasisFor(personnelCategory)}: the appointee's eligibility "
                + $"must be attested before the appointment is recorded. {EligibilityStatementFor(personnelCategory)}");
        }

        EvidenceRoomId = evidenceRoomId;
        PersonnelCategory = personnelCategory;
        UserId = userId;
        AppointmentType = appointmentType;
        EffectiveFrom = effectiveFrom;
        AppointmentOrderReference = Guard.NotBlank(appointmentOrderReference, "IAM-005", "Appointment order reference");
        AppointingAuthority = Guard.NotBlank(appointingAuthority, "IAM-005", "Appointing authority");
        EligibilityAttested = true;
        RecordedByUserId = recordedByUserId;
        RecordedAtUtc = recordedAtUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int EvidenceRoomId { get; private set; }
    public int UserId { get; private set; }
    public User? User { get; private set; }

    public CustodianAppointmentType AppointmentType { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }

    /// <summary>Null while the appointment is open-ended (1-7b: maintained as long as the position is held).</summary>
    public DateTimeOffset? EffectiveTo { get; private set; }

    /// <summary>AR 195-5 1-7b — the written appointment document.</summary>
    public string AppointmentOrderReference { get; private set; } = string.Empty;

    public string AppointingAuthority { get; private set; } = string.Empty;

    /// <summary>
    /// Decides which AR 195-5 1-7a eligibility rule applies. Recorded so an inspector can see
    /// which rule the attestation was made under (IAM-008).
    /// </summary>
    public PersonnelCategory PersonnelCategory { get; private set; }

    /// <summary>Attested, because EMC cannot verify credentialing or a commander's discretion.</summary>
    public bool EligibilityAttested { get; private set; }

    /// <summary>The AR 195-5 paragraph the eligibility attestation was made under.</summary>
    public string EligibilityRegulatoryBasis => EligibilityBasisFor(PersonnelCategory);

    /// <summary>The statement the attesting user affirmed.</summary>
    public string EligibilityStatement => EligibilityStatementFor(PersonnelCategory);

    /// <summary>
    /// AR 195-5 1-7a(1)(c) applies to military CI custodians; 1-7a(2)(c) to civilians. Keeping
    /// these separate matters: the civilian CI paragraph states no credentialing requirement, no
    /// job-series list and no background-investigation requirement, and EMC must not import
    /// restrictions the regulation does not state for CI units.
    /// </summary>
    public static string EligibilityBasisFor(PersonnelCategory category)
        => category switch
        {
            PersonnelCategory.MilitaryCi => "para 1-7a(1)(c)",
            PersonnelCategory.Civilian => "para 1-7a(2)(c)",
            _ => throw new DomainRuleViolationException(
                "IAM-008", $"Unknown personnel category {category}.")
        };

    public static string EligibilityStatementFor(PersonnelCategory category)
        => category switch
        {
            PersonnelCategory.MilitaryCi =>
                "The appointee is a credentialed CI agent and is not in a probationary program.",
            PersonnelCategory.Civilian =>
                "This civilian is appointed as primary or alternate evidence custodian depending "
                + "on the needs and requirements of the unit and at the discretion of the commander.",
            _ => throw new DomainRuleViolationException(
                "IAM-008", $"Unknown personnel category {category}.")
        };

    /// <summary>AR 195-5 1-4i — emergency alternate orders supersede the previous alternate's.</summary>
    public int? SupersedesAppointmentId { get; private set; }

    public int? SupersededByAppointmentId { get; private set; }

    public int RecordedByUserId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public string? Notes { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>True when this appointment confers custodial authority at <paramref name="at"/>.</summary>
    public bool IsActiveAt(DateTimeOffset at)
        => EffectiveFrom <= at
           && (EffectiveTo is null || EffectiveTo > at)
           && SupersededByAppointmentId is null;

    /// <summary>Days this appointment has been in force. Not the AR 195-5 1-4i window.</summary>
    public int DaysAppointedAt(DateTimeOffset at)
        => at < EffectiveFrom ? 0 : (int)(at - EffectiveFrom).TotalDays;

    public void End(DateTimeOffset effectiveTo, string? notes = null)
    {
        if (effectiveTo < EffectiveFrom)
        {
            throw new DomainRuleViolationException(
                "IAM-004", "An appointment cannot end before it becomes effective.");
        }

        EffectiveTo = effectiveTo;
        Notes = Guard.TrimToNull(notes) ?? Notes;
    }

    /// <summary>AR 195-5 1-4i — record that new orders supersede this appointment.</summary>
    public void SupersededBy(CustodianAppointment replacement, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (replacement.AppointmentType != AppointmentType)
        {
            throw new DomainRuleViolationException(
                "IAM-007",
                "AR 195-5 1-4i: superseding orders must be for the same appointment type.");
        }

        SupersededByAppointmentId = replacement.Id;
        replacement.SupersedesAppointmentId = Id;
        EffectiveTo ??= at;
    }
}
