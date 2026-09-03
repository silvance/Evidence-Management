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
        bool eligibilityAttested = true)
        => new(
            evidenceRoomId: 1,
            userId: 7,
            appointmentType: type,
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
    public void AlternateWindow_IsMeasuredAgainstTheThirtyDayThreshold()
    {
        // AR 195-5 1-4i: a temporary absence is "more than 1 working day and not more than 30
        // consecutive days". Para 3-2d then requires that if the absence is known to exceed 30
        // days the alternate be appointed primary on orders and a joint inventory conducted.
        //
        // Whether EMC blocks or warns at the boundary is open decision DEC-05; the domain
        // measures, and the authorization layer decides (IAM-006).
        var alternate = New(CustodianAppointmentType.Alternate);

        Assert.False(alternate.ExceedsAlternateWindowAt(Start.AddDays(30)));
        Assert.True(alternate.ExceedsAlternateWindowAt(Start.AddDays(31)));
        Assert.Equal(31, alternate.ConsecutiveDaysActiveAt(Start.AddDays(31)));
    }

    [Fact]
    public void ThePrimaryAppointmentHasNoSuchWindow()
    {
        // The 30-day limit in 1-4i is about the ALTERNATE acting during the primary's absence.
        // A primary appointment runs until it is ended or superseded.
        var primary = New();

        Assert.False(primary.ExceedsAlternateWindowAt(Start.AddDays(400)));
    }
}
