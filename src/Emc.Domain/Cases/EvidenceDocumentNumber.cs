using System.Globalization;
using System.Text.RegularExpressions;
using Emc.Domain.Common;

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
/// Requirement VCH-004.
/// </summary>
public sealed partial record EvidenceDocumentNumber
{
    private EvidenceDocumentNumber(int sequence, int twoDigitYear)
    {
        Sequence = sequence;
        TwoDigitYear = twoDigitYear;
    }

    /// <summary>The document sequence within the calendar year, beginning at 001 (AR 195-5 2-4c).</summary>
    public int Sequence { get; }

    /// <summary>The two-digit calendar year as written on the form (AR 195-5 2-4c).</summary>
    public int TwoDigitYear { get; }

    /// <summary>
    /// Four-digit year, resolved against a pivot. The regulation writes only two digits, so the
    /// century must be inferred; a 50-year sliding window keeps the mapping stable and sane.
    /// </summary>
    public int CalendarYear
    {
        get
        {
            var currentYear = DateTimeOffset.UtcNow.Year;
            var century = currentYear / 100 * 100;
            var candidate = century + TwoDigitYear;
            if (candidate > currentYear + 50)
            {
                candidate -= 100;
            }
            else if (candidate < currentYear - 50)
            {
                candidate += 100;
            }

            return candidate;
        }
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Sequence:D3}-{TwoDigitYear:D2}");

    public static EvidenceDocumentNumber Parse(string value)
        => TryParse(value, out var parsed)
            ? parsed
            : throw new DomainRuleViolationException(
                "VCH-004",
                $"AR 195-5 2-4c: the evidence document number must be three digits, a hyphen, then a "
                + $"two-digit calendar year (for example 037-26). Received '{value}'.");

    public static bool TryParse(string? value, out EvidenceDocumentNumber parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = DocumentNumberPattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var sequence = int.Parse(match.Groups["seq"].Value, CultureInfo.InvariantCulture);

        // 2-4c begins the series at 001. A sequence of 000 is not a valid document number.
        if (sequence == 0)
        {
            return false;
        }

        var year = int.Parse(match.Groups["yy"].Value, CultureInfo.InvariantCulture);
        parsed = new EvidenceDocumentNumber(sequence, year);
        return true;
    }

    [GeneratedRegex(@"^(?<seq>\d{3})-(?<yy>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentNumberPattern();
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
