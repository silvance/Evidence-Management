using System.Globalization;
using System.Text.RegularExpressions;
using Emc.Domain.Common;
using Emc.Domain.Storage;

namespace Emc.Domain.Cases;

/// <summary>
/// The AR 195-5 2-4c evidence document number.
///
/// "This document number will consist of two groups of digits, separated by a hyphen. The first
/// group is the number of the document beginning with the number 001 for the first DA Form 4137
/// received for the calendar year; the second group will represent the current calendar year
/// (for example, 001-18). The number is assigned by order of precedence from the evidence
/// ledger (or approved automatic equivalent, see para 2-5c)."
///
/// IDENTITY IS CANONICAL: (Sequence, four-digit CalendarYear), scoped to an evidence room. The
/// text as written is carried alongside as <see cref="DisplayNumber"/> and is presentation - it
/// depends on the room's <see cref="EvidenceRoomNumberingPolicy"/>, which is why "001-26" and
/// "26-01" can be the same number.
///
/// THE CALENDAR YEAR IS RESOLVED WHEN THE NUMBER IS RECORDED, FROM CONTEXT - never from the
/// clock. An earlier version derived the century from DateTimeOffset.UtcNow with a sliding
/// 50-year pivot, which meant a domain object's meaning changed with the date the software
/// happened to run. The regulation writes two digits; the four-digit year comes from the date
/// the evidence was received, and if the digits written disagree with that, the custodian
/// confirms the year explicitly rather than the software guessing (VCH-022).
///
/// Requirement VCH-004.
/// </summary>
public sealed record EvidenceDocumentNumber
{
    private EvidenceDocumentNumber(int sequence, int calendarYear, string displayNumber)
    {
        Sequence = sequence;
        CalendarYear = calendarYear;
        DisplayNumber = displayNumber;
    }

    /// <summary>The document sequence within the calendar year, beginning at 001 (AR 195-5 2-4c).</summary>
    public int Sequence { get; }

    /// <summary>The four-digit calendar year, resolved at recording time. Never re-derived.</summary>
    public int CalendarYear { get; }

    /// <summary>The number exactly as the custodian wrote it - "037-26", or "26-37" under a local layout.</summary>
    public string DisplayNumber { get; }

    /// <summary>The two digits the regulation writes for the year.</summary>
    public int TwoDigitYear => CalendarYear % 100;

    public override string ToString() => DisplayNumber;

    /// <summary>
    /// Parses a number written under <paramref name="policy"/>, resolving the four-digit year
    /// from <paramref name="contextCalendarYear"/> - the year of the date the evidence was
    /// received, which is when 2-4c assigns the number.
    ///
    /// If the digits written do not end the context year, nothing is guessed: the result asks
    /// for the year to be confirmed, and a caller supplies <paramref name="confirmedCalendarYear"/>,
    /// which must itself end in the digits written.
    /// </summary>
    public static DocumentNumberParseResult Parse(
        string? raw,
        EvidenceRoomNumberingPolicy policy,
        int contextCalendarYear,
        int? confirmedCalendarYear = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.TryParseComponents(raw, out var sequence, out var yearDigits))
        {
            return DocumentNumberParseResult.Failure(
                "VCH-004",
                $"The evidence document number for this evidence room is written as "
                + $"{policy.Describe()}. Received '{raw}'.");
        }

        var text = raw!.Trim();

        if (policy.YearWidth == 4)
        {
            if (confirmedCalendarYear is not null && confirmedCalendarYear != yearDigits)
            {
                return DocumentNumberParseResult.Failure(
                    "VCH-022",
                    $"The number is written with the year {yearDigits}, which is not the confirmed "
                    + $"year {confirmedCalendarYear}.");
            }

            return DocumentNumberParseResult.Success(new EvidenceDocumentNumber(sequence, yearDigits, text));
        }

        if (confirmedCalendarYear is int confirmed)
        {
            if (confirmed % 100 != yearDigits)
            {
                return DocumentNumberParseResult.Failure(
                    "VCH-022",
                    $"The number is written with the year digits {yearDigits:D2}, and the confirmed "
                    + $"calendar year {confirmed} does not end in them. Check the number against "
                    + "the evidence ledger.");
            }

            return DocumentNumberParseResult.Success(new EvidenceDocumentNumber(sequence, confirmed, text));
        }

        if (contextCalendarYear % 100 == yearDigits)
        {
            return DocumentNumberParseResult.Success(
                new EvidenceDocumentNumber(sequence, contextCalendarYear, text));
        }

        return DocumentNumberParseResult.RequiresConfirmation(
            "VCH-022",
            $"The number is written with the year digits {yearDigits:D2}, but the evidence was "
            + $"received in {contextCalendarYear}. AR 195-5 para 2-4c numbers the form from the "
            + "calendar year it is received in. If the number is correct as written - for "
            + "example a form from a prior year being entered now - confirm the four-digit "
            + "calendar year it belongs to; otherwise check the number against the evidence "
            + "ledger.");
    }

    /// <summary>
    /// A number in the regulation's own layout with an explicitly stated calendar year. For
    /// seeding and tests; the application path goes through <see cref="Parse"/>.
    /// </summary>
    public static EvidenceDocumentNumber Regulatory(string raw, int calendarYear)
    {
        var result = Parse(
            raw, EvidenceRoomNumberingPolicy.Regulatory(0, DateTimeOffset.MinValue), calendarYear);

        return result.Number
            ?? throw new DomainRuleViolationException(result.RequirementId!, result.Error!);
    }
}

/// <summary>
/// The outcome of reading a written document number. Three cases: a number; a malformed entry;
/// or a well-formed entry whose two-digit year does not match the year of receipt, which the
/// custodian must confirm rather than the software resolve (VCH-022).
/// </summary>
public sealed record DocumentNumberParseResult(
    EvidenceDocumentNumber? Number,
    string? RequirementId,
    string? Error,
    bool CalendarYearRequiresConfirmation)
{
    public bool Succeeded => Number is not null;

    public static DocumentNumberParseResult Success(EvidenceDocumentNumber number)
        => new(number, null, null, false);

    public static DocumentNumberParseResult Failure(string requirementId, string error)
        => new(null, requirementId, error, false);

    public static DocumentNumberParseResult RequiresConfirmation(string requirementId, string error)
        => new(null, requirementId, error, true);
}

/// <summary>
/// The EMC-generated temporary identifier used before a custodian assigns the official number.
///
/// AR 195-5 2-4c requires the custodian to assign the official number by order of precedence
/// from the evidence ledger, and 2-5a requires that ledger to be a bound book absent the
/// approval described in 2-5c (for CI organizations, Army G-2X). EMC V1 therefore must NOT
/// generate the official number (EMC-002).
///
/// Format: TMP-yyyyMMdd-Annn. Deliberately unlike the regulatory NNN-YY format so the two can
/// never be confused on a screen, in a search box, or on a printout (VCH-003).
/// </summary>
public sealed partial record TemporaryEvidenceIdentifier
{
    private TemporaryEvidenceIdentifier(DateOnly date, char block, int sequence)
    {
        Date = date;
        Block = block;
        Sequence = sequence;
    }

    public DateOnly Date { get; }
    public char Block { get; }
    public int Sequence { get; }

    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"TMP-{Date:yyyyMMdd}-{Block}{Sequence:D3}");

    public static TemporaryEvidenceIdentifier Create(DateOnly date, int ordinalWithinDay)
    {
        Guard.Positive(ordinalWithinDay, "VCH-003", "Ordinal within day");

        // 26 blocks of 999 gives 25,974 draft vouchers per evidence room per day, which is far
        // beyond any realistic volume. Exceeding it means something is wrong, so fail loudly.
        const int perBlock = 999;
        var blockIndex = (ordinalWithinDay - 1) / perBlock;
        if (blockIndex > 25)
        {
            throw new DomainRuleViolationException(
                "VCH-003", "Temporary identifier capacity exhausted for this date.");
        }

        var sequence = ((ordinalWithinDay - 1) % perBlock) + 1;
        return new TemporaryEvidenceIdentifier(date, (char)('A' + blockIndex), sequence);
    }

    public static bool TryParse(string? value, out TemporaryEvidenceIdentifier parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = TemporaryIdentifierPattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!DateOnly.TryParseExact(
                match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return false;
        }

        var sequence = int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture);
        if (sequence == 0)
        {
            return false;
        }

        parsed = new TemporaryEvidenceIdentifier(date, match.Groups["block"].Value[0], sequence);
        return true;
    }

    [GeneratedRegex(
        @"^TMP-(?<date>\d{8})-(?<block>[A-Z])(?<seq>\d{3})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryIdentifierPattern();
}
