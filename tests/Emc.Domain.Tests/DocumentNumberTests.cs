using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Storage;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 para 2-4c - the evidence document number.
/// Requirements: VCH-004, VCH-003, VCH-022, EMC-002.
/// </summary>
public class DocumentNumberTests
{
    private static readonly EvidenceRoomNumberingPolicy Regulatory =
        EvidenceRoomNumberingPolicy.Regulatory(1, DateTimeOffset.MinValue);

    [Theory]
    [InlineData("001-18", 1, 2018)]
    [InlineData("037-26", 37, 2026)]
    [InlineData("999-99", 999, 2099)]
    public void Parse_AcceptsTheFormatTheRegulationPrescribes(string value, int sequence, int calendarYear)
    {
        // AR 195-5 2-4c: "two groups of digits, separated by a hyphen. The first group is the
        // number of the document beginning with the number 001 for the first DA Form 4137
        // received for the calendar year; the second group will represent the current calendar
        // year (for example, 001-18)."
        var result = EvidenceDocumentNumber.Parse(value, Regulatory, contextCalendarYear: calendarYear);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(sequence, result.Number!.Sequence);
        Assert.Equal(calendarYear, result.Number.CalendarYear);
        Assert.Equal(calendarYear % 100, result.Number.TwoDigitYear);
        Assert.Equal(value, result.Number.DisplayNumber);
        Assert.Equal(value, result.Number.ToString());
    }

    [Theory]
    [InlineData("1-18")]
    [InlineData("001-2018")]
    [InlineData("001/18")]
    [InlineData("00118")]
    [InlineData("ABC-18")]
    [InlineData("000-18")]
    [InlineData("26-01")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RejectsAnythingElse_DescribingTheRoomsLayout(string? value)
    {
        var result = EvidenceDocumentNumber.Parse(value, Regulatory, 2018);

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-004", result.RequirementId);
        Assert.Contains("2-4c", result.Error!, StringComparison.Ordinal);
        Assert.Contains("037-26", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCalendarYearComesFromContext_NotFromTheClock()
    {
        // VCH-022. The regression for the moving-century pivot. The same written number means a
        // different year depending on when the evidence was received - and nothing else. Neither
        // result depends on the date the test runs.
        var in2001 = EvidenceDocumentNumber.Parse("001-01", Regulatory, contextCalendarYear: 2001);
        var in2101 = EvidenceDocumentNumber.Parse("001-01", Regulatory, contextCalendarYear: 2101);

        Assert.Equal(2001, in2001.Number!.CalendarYear);
        Assert.Equal(2101, in2101.Number!.CalendarYear);
    }

    [Fact]
    public void AYearThatDisagreesWithTheContextIsNotGuessed()
    {
        // VCH-022. Received in 2026, written "-25". Possibly a prior-year form entered late;
        // possibly a slip of the pen. The software does not decide which.
        var result = EvidenceDocumentNumber.Parse("012-25", Regulatory, contextCalendarYear: 2026);

        Assert.False(result.Succeeded);
        Assert.True(result.CalendarYearRequiresConfirmation);
        Assert.Equal("VCH-022", result.RequirementId);
        Assert.Contains("2026", result.Error!, StringComparison.Ordinal);
        Assert.Contains("confirm", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AConfirmedYearResolvesTheDisagreement()
    {
        var result = EvidenceDocumentNumber.Parse(
            "012-25", Regulatory, contextCalendarYear: 2026, confirmedCalendarYear: 2025);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2025, result.Number!.CalendarYear);
        Assert.Equal(12, result.Number.Sequence);
        Assert.Equal("012-25", result.Number.DisplayNumber);
    }

    [Fact]
    public void AConfirmedYearMustEndInTheDigitsWritten()
    {
        // Confirming 2024 for a number written "-25" is a contradiction, not a confirmation.
        var result = EvidenceDocumentNumber.Parse(
            "012-25", Regulatory, contextCalendarYear: 2026, confirmedCalendarYear: 2024);

        Assert.False(result.Succeeded);
        Assert.False(result.CalendarYearRequiresConfirmation);
        Assert.Equal("VCH-022", result.RequirementId);
    }

    [Fact]
    public void ALocalLayoutYieldsTheSameCanonicalNumber()
    {
        // VCH-023. "26-01" under the local layout IS "001-26" under the regulation's: identity is
        // (year, sequence), and the text as written is carried alongside.
        var local = new EvidenceRoomNumberingPolicy(
            1, DateTimeOffset.MinValue, DocumentNumberLayout.YearThenSequence, 2, 2, "-",
            NumberingPolicyBasis.LocalAuthorized, "SOP 26-1", null);

        var written = EvidenceDocumentNumber.Parse("26-01", local, 2026).Number!;
        var regulatory = EvidenceDocumentNumber.Parse("001-26", Regulatory, 2026).Number!;

        Assert.Equal((regulatory.CalendarYear, regulatory.Sequence), (written.CalendarYear, written.Sequence));
        Assert.Equal("26-01", written.DisplayNumber);
        Assert.Equal("001-26", regulatory.DisplayNumber);
    }

    [Fact]
    public void RegulatoryHelper_ThrowsWithTheRegulatoryCitation()
    {
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => EvidenceDocumentNumber.Regulatory("37-26", 2026));

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
        Assert.False(Regulatory.TryParseComponents(temporary.ToString(), out _, out _));
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
