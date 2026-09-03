using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Storage;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 para 2-4c prescribes "001-26". Some rooms write "26-01". The layout is a per-room
/// policy [LOCAL]; the identity of a number is canonical and layout-independent.
/// Requirements: VCH-004, VCH-022, VCH-023.
/// </summary>
public class NumberingPolicyTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static EvidenceRoomNumberingPolicy Local(
        NumberingPolicyBasis basis = NumberingPolicyBasis.LocalAuthorized,
        string? authority = "902d MI Group Evidence Room SOP 26-1, para 4.")
        => new(
            1, From, DocumentNumberLayout.YearThenSequence,
            sequenceWidth: 2, yearWidth: 2, separator: "-",
            basis, authority, notes: null);

    [Fact]
    public void TheDefaultIsTheRegulationsLayout()
    {
        var policy = EvidenceRoomNumberingPolicy.Regulatory(1, From);

        Assert.True(policy.IsRegulatoryLayout);
        Assert.False(policy.IsAwaitingValidation);
        Assert.Equal(NumberingPolicyBasis.RegulationDefault, policy.Basis);
        Assert.Equal("001-26", policy.Format(1, 2026));
        Assert.Equal("037-26", policy.Example());
        Assert.Contains("AR 195-5 para 2-4c", policy.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalLayoutWritesTheYearFirst()
    {
        var policy = Local();

        Assert.False(policy.IsRegulatoryLayout);
        Assert.Equal("26-01", policy.Format(1, 2026));
        Assert.Equal("26-37", policy.Example());

        // The description never claims the regulation prescribes this.
        Assert.Contains("AR 195-5 para 2-4c prescribes 001-26", policy.Describe(), StringComparison.Ordinal);
        Assert.Contains("SOP 26-1", policy.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("001-26", 1, 26)]
    [InlineData("037-26", 37, 26)]
    [InlineData("1000-26", 1000, 26)]   // the sequence may exceed its width; the year may not
    public void TheRegulatoryLayoutReadsItsOwnNumbers(string raw, int sequence, int year)
    {
        Assert.True(EvidenceRoomNumberingPolicy.Regulatory(1, From).TryParseComponents(raw, out var s, out var y));
        Assert.Equal(sequence, s);
        Assert.Equal(year, y);
    }

    [Theory]
    [InlineData("26-01", 1, 26)]
    [InlineData("26-37", 37, 26)]
    [InlineData("26-100", 100, 26)]
    public void ALocalLayoutReadsItsOwnNumbers(string raw, int sequence, int year)
    {
        Assert.True(Local().TryParseComponents(raw, out var s, out var y));
        Assert.Equal(sequence, s);
        Assert.Equal(year, y);
    }

    [Theory]
    [InlineData("26-01")]      // the local layout, offered to the regulatory policy
    [InlineData("1-26")]       // sequence narrower than its width
    [InlineData("001-2026")]   // year wider than its width
    [InlineData("001/26")]
    [InlineData("00126")]
    [InlineData("ABC-26")]
    [InlineData("000-26")]     // 2-4c begins the series at 001
    [InlineData("001-26-1")]
    [InlineData("")]
    [InlineData(null)]
    public void TheRegulatoryLayoutRejectsAnythingElse(string? raw)
        => Assert.False(EvidenceRoomNumberingPolicy.Regulatory(1, From).TryParseComponents(raw, out _, out _));

    [Fact]
    public void ALocalLayoutRejectsTheRegulatoryOne()
    {
        // "001-26" read year-first would be year "001": three digits where two are expected.
        // Exact year width is what keeps the two layouts from being confused for each other.
        Assert.False(Local().TryParseComponents("001-26", out _, out _));
    }

    [Fact]
    public void ANonRegulatoryLayoutCannotClaimTheRegulationAsItsBasis()
    {
        // VCH-023. The honesty rule. Whatever a room does, EMC will not record "26-01" as what
        // AR 195-5 prescribes.
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => new EvidenceRoomNumberingPolicy(
                1, From, DocumentNumberLayout.YearThenSequence, 2, 2, "-",
                NumberingPolicyBasis.RegulationDefault, null, null));

        Assert.Equal("VCH-023", ex.RequirementId);
        Assert.Contains("001-26", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocallyAuthorizedLayoutMustCiteItsAuthority()
    {
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => Local(NumberingPolicyBasis.LocalAuthorized, authority: "  "));

        Assert.Equal("VCH-023", ex.RequirementId);
    }

    [Fact]
    public void ALegacyLayoutNeedsNoAuthorityButIsFlagged()
    {
        var policy = Local(NumberingPolicyBasis.LegacyObserved, authority: null);

        Assert.True(policy.IsAwaitingValidation);
        Assert.Contains("awaiting validation", policy.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void PoliciesAreEffectiveDated()
    {
        var policy = EvidenceRoomNumberingPolicy.Regulatory(1, From);

        Assert.False(policy.IsEffectiveAt(From.AddSeconds(-1)));
        Assert.True(policy.IsEffectiveAt(From));

        policy.EndAt(From.AddMonths(6));
        Assert.True(policy.IsEffectiveAt(From.AddMonths(6).AddSeconds(-1)));
        Assert.False(policy.IsEffectiveAt(From.AddMonths(6)));

        Assert.Throws<DomainRuleViolationException>(() => policy.EndAt(From));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void TheSequenceWidthIsBounded(int width)
        => Assert.Throws<DomainRuleViolationException>(() => new EvidenceRoomNumberingPolicy(
            1, From, DocumentNumberLayout.SequenceThenYear, width, 2, "-",
            NumberingPolicyBasis.LocalAuthorized, "SOP", null));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("----")]
    public void TheSeparatorIsShortAndNotADigit(string separator)
        => Assert.Throws<DomainRuleViolationException>(() => new EvidenceRoomNumberingPolicy(
            1, From, DocumentNumberLayout.SequenceThenYear, 3, 2, separator,
            NumberingPolicyBasis.LocalAuthorized, "SOP", null));

    [Fact]
    public void AFourDigitYearLayoutIsSupported()
    {
        var policy = new EvidenceRoomNumberingPolicy(
            1, From, DocumentNumberLayout.SequenceThenYear, 3, 4, "-",
            NumberingPolicyBasis.LocalAuthorized, "SOP", null);

        Assert.Equal("037-2026", policy.Format(37, 2026));
        Assert.True(policy.TryParseComponents("037-2026", out var s, out var y));
        Assert.Equal(37, s);
        Assert.Equal(2026, y);
    }
}
