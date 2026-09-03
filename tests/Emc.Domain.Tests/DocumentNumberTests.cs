using Emc.Domain.Cases;
using Emc.Domain.Common;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 para 2-4c - the evidence document number.
/// Requirements: VCH-004, VCH-003, EMC-002.
/// </summary>
public class DocumentNumberTests
{
    [Theory]
    [InlineData("001-18", 1, 18)]
    [InlineData("037-26", 37, 26)]
    [InlineData("999-99", 999, 99)]
    public void Parse_AcceptsTheFormatTheRegulationPrescribes(string value, int sequence, int year)
    {
        // AR 195-5 2-4c: "two groups of digits, separated by a hyphen. The first group is the
        // number of the document beginning with the number 001 for the first DA Form 4137
        // received for the calendar year; the second group will represent the current calendar
        // year (for example, 001-18)."
        var parsed = EvidenceDocumentNumber.Parse(value);

        Assert.Equal(sequence, parsed.Sequence);
        Assert.Equal(year, parsed.TwoDigitYear);
        Assert.Equal(value, parsed.ToString());
    }

    [Theory]
    [InlineData("1-18")]        // sequence not three digits
    [InlineData("0001-18")]     // sequence too long
    [InlineData("001-2018")]    // year not two digits
    [InlineData("001/18")]      // wrong separator
    [InlineData("00118")]       // no separator
    [InlineData("ABC-18")]      // not digits
    [InlineData("000-18")]      // 2-4c begins the series at 001
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsAnythingElse(string? value)
        => Assert.False(EvidenceDocumentNumber.TryParse(value, out _));

    [Fact]
    public void Parse_ThrowsWithTheRegulatoryCitation()
    {
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => EvidenceDocumentNumber.Parse("37-26"));

        Assert.Equal("VCH-004", ex.RequirementId);
        Assert.Contains("2-4c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporaryIdentifier_IsVisuallyDistinctFromTheRegulatoryFormat()
    {
        // VCH-003. The temporary identifier must never be mistakable for an AR 195-5 2-4c
        // document number on a screen, in a search box, or on a printout.
        var temporary = TemporaryEvidenceIdentifier.Create(new DateOnly(2026, 9, 3), 14);

        Assert.Equal("TMP-20260903-A014", temporary.ToString());
        Assert.False(EvidenceDocumentNumber.TryParse(temporary.ToString(), out _));
    }

    [Fact]
    public void TemporaryIdentifier_RollsIntoTheNextBlockAfter999()
    {
        Assert.Equal("TMP-20260903-A999", TemporaryEvidenceIdentifier.Create(new DateOnly(2026, 9, 3), 999).ToString());
        Assert.Equal("TMP-20260903-B001", TemporaryEvidenceIdentifier.Create(new DateOnly(2026, 9, 3), 1000).ToString());
    }

    [Fact]
    public void TemporaryIdentifier_RoundTrips()
    {
        var original = TemporaryEvidenceIdentifier.Create(new DateOnly(2026, 9, 3), 14);

        Assert.True(TemporaryEvidenceIdentifier.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original.ToString(), parsed.ToString());
    }
}
