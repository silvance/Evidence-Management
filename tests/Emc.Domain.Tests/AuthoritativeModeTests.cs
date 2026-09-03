using Emc.Domain.Common;
using Emc.Domain.Configuration;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 para 2-5c - the constraint that defines what V1 may be.
/// Requirements: EMC-001, EMC-002, EMC-003, EMC-004, EMC-005.
/// </summary>
public class AuthoritativeModeTests
{
    private static SystemConfiguration NewConfiguration()
        => new("Test CI Unit", "UNCLASSIFIED");

    [Fact]
    public void DefaultsToCompanionMode()
    {
        // AR 195-5 2-5c: a STAND-ALONE automated evidence ledger/accountability system requires
        // prior approval - for CI organizations, from Army G-2X. A system "used in conjunction
        // with or to enhance the requirements of this regulation" does not. V1 is the latter.
        var configuration = NewConfiguration();

        Assert.Equal(AuthoritativeMode.Companion, configuration.AuthoritativeMode);
        Assert.Equal(NumberingMode.ManualTranscription, configuration.NumberingMode);
    }

    [Fact]
    public void CompanionMode_RefusesSystemAssignedNumbering()
    {
        // EMC-002. AR 195-5 2-4c makes assignment the custodian's act, performed by order of
        // precedence from the evidence ledger, which 2-5a requires to be a bound book absent
        // approval under 2-5c.
        var configuration = NewConfiguration();

        var ex = Assert.Throws<DomainRuleViolationException>(
            configuration.EnableSystemAssignedNumbering);

        Assert.Equal("EMC-002", ex.RequirementId);
        Assert.Contains("G-2X", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingMode_RequiresARecordedApprovalReference()
    {
        // EMC-004, EMC-005. The upgrade path is a configuration change, but it is gated on the
        // approval AR 195-5 2-5c requires, and the reference is retained.
        var configuration = NewConfiguration();

        Assert.Throws<DomainRuleViolationException>(
            () => configuration.EnableAuthoritativeLedgerMode(" ", DateTimeOffset.UtcNow));

        configuration.EnableAuthoritativeLedgerMode("G2X-APPROVAL-2027-004", DateTimeOffset.UtcNow);
        configuration.EnableSystemAssignedNumbering();

        Assert.Equal(AuthoritativeMode.AuthoritativeLedger, configuration.AuthoritativeMode);
        Assert.Equal(NumberingMode.SystemAssigned, configuration.NumberingMode);
        Assert.Equal("G2X-APPROVAL-2027-004", configuration.AutomatedSystemApprovalReference);
    }

    [Fact]
    public void CompanionNotice_NamesTheAuthoritativeRecords()
    {
        // EMC-003. Every accountability view must be unambiguous about which record is
        // authoritative: the bound ledger (2-5a) and the original DA Form 4137 (2-4d).
        var notice = NewConfiguration().AuthoritativeRecordNotice;

        Assert.Contains("COMPANION", notice, StringComparison.Ordinal);
        Assert.Contains("bound evidence ledger", notice, StringComparison.Ordinal);
        Assert.Contains("original DA Form 4137", notice, StringComparison.Ordinal);
        Assert.Contains("2-5a", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void SuspenseThreshold_IsLocal_NotRegulatory()
    {
        // SUSP-004. AR 195-5 gives NO numeric limit for any temporary-release category: 2-7a
        // requires "reasonable and adequate contact" and 2-7b/3-1a(4) require that release not be
        // for "an excessive period". The threshold is configurable precisely because it is local
        // policy, not regulation.
        var configuration = NewConfiguration();
        configuration.SetLocalSuspenseReviewThreshold(45);

        Assert.Equal(45, configuration.LocalSuspenseReviewThresholdDays);
        Assert.Throws<DomainRuleViolationException>(
            () => configuration.SetLocalSuspenseReviewThreshold(0));
    }
}
