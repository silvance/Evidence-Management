using Emc.Domain.Common;
using Emc.Domain.Identity;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 paras 1-4g(1), 1-4i, 1-7a(1)(c), 1-7b - custodial authority comes from a written
/// appointment, not from a role.
/// Requirements: IAM-004 .. IAM-008. Invariant I-06.
/// </summary>
public class CustodianAppointmentTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static CustodianAppointment New(
        CustodianAppointmentType type = CustodianAppointmentType.Primary,
        DateTimeOffset? from = null,
        bool eligibilityAttested = true,
        PersonnelCategory category = PersonnelCategory.MilitaryCi)
        => new(
            evidenceRoomId: 1,
            userId: 7,
            appointmentType: type,
            personnelCategory: category,
            effectiveFrom: from ?? Start,
            appointmentOrderReference: "ORDERS 2026-114, 902d MI Group",
            appointingAuthority: "Commander, 902d MI Group",
            eligibilityAttested: eligibilityAttested,
            recordedByUserId: 1,
            recordedAtUtc: Start);

    [Fact]
    public void Appointment_RequiresTheEligibilityAttestation()
    {
        // AR 195-5 1-7a(1)(c): the CI evidence custodian "must be a credentialed CI agent" and
        // "CI Agents in a probationary program will not be appointed". EMC cannot verify
        // credentialing, so the recording user attests to it and the attestation is retained
        // (IAM-008).
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => New(eligibilityAttested: false));

        Assert.Equal("IAM-008", ex.RequirementId);
        Assert.Contains("1-7a(1)(c)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAppointmentConfersAuthorityOnlyWithinItsEffectiveRange()
    {
        // IAM-005, invariant I-11. A role flag cannot express this; a dated appointment can.
        var appointment = New();

        Assert.False(appointment.IsActiveAt(Start.AddDays(-1)));
        Assert.True(appointment.IsActiveAt(Start.AddDays(5)));

        appointment.End(Start.AddDays(10));

        Assert.True(appointment.IsActiveAt(Start.AddDays(9)));
        Assert.False(appointment.IsActiveAt(Start.AddDays(11)));
    }

    [Fact]
    public void AnAppointmentCannotEndBeforeItBegins()
    {
        var appointment = New();
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => appointment.End(Start.AddDays(-1)));

        Assert.Equal("IAM-004", ex.RequirementId);
    }

    [Fact]
    public void EmergencyOrders_SupersedeThePreviousAlternate()
    {
        // AR 195-5 1-4i: "If the alternate evidence custodian has a temporary absence due to an
        // emergency situation ... the commanders/SACs/RACs may appoint, in writing, another
        // alternate evidence custodian. The appointment orders will supersede the previous
        // alternate evidence custodian's orders" (IAM-007).
        var original = New(CustodianAppointmentType.Alternate);
        var replacement = New(CustodianAppointmentType.Alternate, Start.AddDays(3));

        original.SupersededBy(replacement, Start.AddDays(3));

        Assert.False(original.IsActiveAt(Start.AddDays(4)));
        Assert.True(replacement.IsActiveAt(Start.AddDays(4)));
    }

    [Fact]
    public void SupersessionRequiresTheSameAppointmentType()
    {
        // An alternate's orders supersede an alternate's orders. A primary appointment is a
        // different act under 1-4g(1) and does not supersede an alternate's.
        var alternate = New(CustodianAppointmentType.Alternate);
        var primary = New(CustodianAppointmentType.Primary, Start.AddDays(3));

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => alternate.SupersededBy(primary, Start.AddDays(3)));

        Assert.Equal("IAM-007", ex.RequirementId);
    }

    [Fact]
    public void MilitaryCiEligibilityCitesParagraph1_7a1c()
    {
        // AR 195-5 1-7a(1)(c): "the CI evidence custodian (primary and alternate) must be a
        // credentialed CI agent. CI Agents in a probationary program will not be appointed."
        var appointment = New(category: PersonnelCategory.MilitaryCi);

        Assert.Equal("para 1-7a(1)(c)", appointment.EligibilityRegulatoryBasis);
        Assert.Contains("credentialed CI agent", appointment.EligibilityStatement, StringComparison.Ordinal);
        Assert.Contains("probationary", appointment.EligibilityStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void CivilianEligibilityCitesParagraph1_7a2c_AndImportsNoExtraRestrictions()
    {
        // AR 195-5 1-7a(2)(c): "Civilians may be appointed as the primary or alternate evidence
        // custodian, depending on the needs and requirements of the unit and at the discretion of
        // the commander."
        //
        // The earlier model wrongly required EVERY CI custodian to attest to being a credentialed,
        // non-probationary CI agent, which would have made a lawful civilian appointment
        // impossible to record truthfully.
        //
        // Note what this paragraph does NOT say: unlike the USACIDC and Military Police civilian
        // paragraphs, it states no job-series list and no background-investigation requirement,
        // and EMC must not import those into the CI case (IAM-008).
        var appointment = New(category: PersonnelCategory.Civilian);

        Assert.Equal("para 1-7a(2)(c)", appointment.EligibilityRegulatoryBasis);
        Assert.Contains("discretion", appointment.EligibilityStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialed CI agent", appointment.EligibilityStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("probationary", appointment.EligibilityStatement, StringComparison.Ordinal);
        Assert.DoesNotContain("background investigation", appointment.EligibilityStatement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GS-", appointment.EligibilityStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void ACivilianCanBeAppointedPrimaryCustodian()
    {
        // The regression the correction is really about: a lawful civilian CI custodian
        // appointment must be recordable.
        var appointment = New(
            type: CustodianAppointmentType.Primary, category: PersonnelCategory.Civilian);

        Assert.True(appointment.EligibilityAttested);
        Assert.Equal(PersonnelCategory.Civilian, appointment.PersonnelCategory);
        Assert.True(appointment.IsActiveAt(Start.AddDays(1)));
    }

    [Fact]
    public void TheAppointmentNoLongerCarriesTheThirtyDayWindow()
    {
        // AR 195-5 1-4i's 30-day limit applies to the alternate ACTING in the primary's absence,
        // not to how long the alternate has held the appointment. An alternate may be appointed
        // for years without the primary ever being absent, so the appointment itself has no
        // expiry - that lives on CustodianDutyAssumption (IAM-019).
        var alternate = New(CustodianAppointmentType.Alternate);

        Assert.True(alternate.IsActiveAt(Start.AddDays(400)));
        Assert.Equal(400, alternate.DaysAppointedAt(Start.AddDays(400)));
    }
}

/// <summary>
/// AR 195-5 paras 1-4i, 1-7c(1) and 1-7c(2) - the alternate assuming the primary's duties.
/// Requirements: IAM-006, IAM-019, IAM-020.
/// </summary>
public class CustodianDutyAssumptionTests
{
    private static readonly DateTimeOffset Absence = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private static CustodianDutyAssumption New(DateTimeOffset? assumedAt = null)
        => new(
            evidenceRoomId: 1,
            primaryAppointmentId: 10,
            alternateAppointmentId: 11,
            alternateUserId: 7,
            primaryAbsenceStart: Absence,
            alternateAssumedDutiesAt: assumedAt ?? Absence,
            assumptionLedgerAttestation:
                "I CHEN, DAVID L., on 01 SEP 26, assume all duties of the primary evidence "
                + "custodian during the temporary absence of the regularly appointed custodian.",
            recordedByUserId: 4,
            recordedAtUtc: Absence,
            reasonForAbsence: "Temporary duty");

    [Fact]
    public void TheAssumptionRequiresTheLedgerAttestation()
    {
        // AR 195-5 1-7c(1) requires the alternate to ENTER AND SIGN the prescribed statement in
        // the evidence ledger. EMC records that the paper entry was made.
        var ex = Assert.Throws<DomainRuleViolationException>(() => new CustodianDutyAssumption(
            1, 10, 11, 7, Absence, Absence, "   ", 4, Absence));

        Assert.Equal("IAM-019", ex.RequirementId);
    }

    [Fact]
    public void DutiesCannotBeAssumedBeforeTheAbsenceBegins()
    {
        var ex = Assert.Throws<DomainRuleViolationException>(() => new CustodianDutyAssumption(
            1, 10, 11, 7, Absence, Absence.AddDays(-1), "statement", 4, Absence));

        Assert.Equal("IAM-019", ex.RequirementId);
    }

    [Fact]
    public void TheAssumptionIsActiveUntilThePrimaryResumes()
    {
        var assumption = New();

        Assert.True(assumption.IsActiveAt(Absence.AddDays(3)));

        assumption.RecordPrimaryResumption(
            Absence.AddDays(5),
            "I BAKER, ALICE C., on 06 SEP 26, resume my position as primary evidence custodian.",
            4,
            Absence.AddDays(5));

        Assert.True(assumption.IsActiveAt(Absence.AddDays(4)));
        Assert.False(assumption.IsActiveAt(Absence.AddDays(6)));
    }

    [Fact]
    public void TheThirtyDayLimitRunsFromTheDateDutiesWereAssumed()
    {
        // The correction. AR 195-5 1-4i measures the temporary absence, not the appointment.
        // Duties are assumed 100 days after the alternate was appointed; day 30 of ACTING is
        // still within the limit.
        var assumption = New(assumedAt: Absence.AddDays(100));

        Assert.False(assumption.ExceedsTemporaryAbsenceLimitAt(Absence.AddDays(130)));
        Assert.True(assumption.ExceedsTemporaryAbsenceLimitAt(Absence.AddDays(131)));
        Assert.Equal(30, assumption.ConsecutiveDaysAt(Absence.AddDays(130)));
    }

    [Fact]
    public void ThePrimaryCannotResumeTwice()
    {
        var assumption = New();
        assumption.RecordPrimaryResumption(Absence.AddDays(5), "statement", 4, Absence.AddDays(5));

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => assumption.RecordPrimaryResumption(Absence.AddDays(6), "again", 4, Absence.AddDays(6)));

        Assert.Equal("IAM-019", ex.RequirementId);
    }

    [Fact]
    public void AnAbsenceOfThirtyDaysOrLessNeedsNoHundredPercentInventory()
    {
        // AR 195-5 1-7c(2): "If the absence is 30 calendar days or less, there is no requirement
        // to conduct a 100 percent inventory." Beyond that, 3-2d requires a joint inventory.
        var shortAbsence = New();
        shortAbsence.RecordPrimaryResumption(Absence.AddDays(30), "statement", 4, Absence.AddDays(30));
        Assert.False(shortAbsence.RequiresHundredPercentInventoryOnResumption);

        var longAbsence = New();
        longAbsence.RecordPrimaryResumption(Absence.AddDays(45), "statement", 4, Absence.AddDays(45));
        Assert.True(longAbsence.RequiresHundredPercentInventoryOnResumption);
    }
}
