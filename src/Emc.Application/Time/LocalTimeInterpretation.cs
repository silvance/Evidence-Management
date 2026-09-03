using System.Globalization;
using Emc.Domain.Common;

namespace Emc.Application.Time;

/// <summary>What a wall-clock time written on paper turned out to be in the room's zone.</summary>
public enum LocalTimeKind
{
    /// <summary>One instant. The normal case.</summary>
    Valid = 1,

    /// <summary>
    /// Two instants - the hour that repeats when clocks fall back. "01:30" on the first Sunday
    /// of November in a US zone happened twice, an hour apart.
    /// </summary>
    Ambiguous = 2,

    /// <summary>
    /// No instant - the hour skipped when clocks spring forward. "02:30" on the second Sunday of
    /// March in a US zone never happened.
    /// </summary>
    Nonexistent = 3
}

/// <summary>Which of the two occurrences an ambiguous wall-clock time means.</summary>
public enum AmbiguousLocalTimeChoice
{
    /// <summary>Not stated. An ambiguous time is then refused rather than resolved by default.</summary>
    Unspecified = 0,

    /// <summary>The first occurrence, still on daylight time.</summary>
    Earlier = 1,

    /// <summary>The second occurrence, after the clocks fell back to standard time.</summary>
    Later = 2
}

/// <summary>
/// The outcome of interpreting a wall-clock time in an evidence room's zone. Carries the instant
/// with its offset when there is exactly one; otherwise says why not, with the candidates.
/// </summary>
public sealed record LocalTimeResolution(
    LocalTimeKind Kind,
    DateTimeOffset? Value,
    string TimeZoneId,
    IReadOnlyList<DateTimeOffset> Candidates,
    string? RequirementId,
    string? Error)
{
    public bool Succeeded => Value is not null;
}

/// <summary>
/// Interprets the LOCAL date and time written on a DA Form 4137 or in the ledger in the
/// evidence room's own time zone - never the web server's.
///
/// AR 195-5 records local time: the ledger example is "03 SEP 26 09:15" (2-5b), and every
/// chain-of-custody entry carries a date and time (2-3f). EMC stores the instant with the offset
/// it had in the room's zone, so the paper and the record agree (AUD-011). The earlier pages
/// used TimeZoneInfo.Local, which is the zone of whichever IIS host happens to run the
/// application - a UTC server would have recorded "09:15" as 09:15Z and put every event four or
/// five hours off the paper.
///
/// Daylight-saving edge cases are handled explicitly, not by a default:
///   - a time in the repeated hour is AMBIGUOUS and is refused until the custodian says which
///     occurrence is meant;
///   - a time in the skipped hour is NONEXISTENT and is refused, because the software choosing
///     an adjacent time would be inventing a fact about when something happened.
///
/// Pure: no clock, no database. The zone comes from the caller.
///
/// Requirements: AUD-011, AUD-019, AUD-020.
/// </summary>
public static class LocalTimeInterpretation
{
    public static LocalTimeResolution Interpret(
        DateTime wallClock,
        TimeZoneInfo zone,
        AmbiguousLocalTimeChoice choice = AmbiguousLocalTimeChoice.Unspecified)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // What was typed is a wall-clock reading with no zone of its own. Any Kind the caller
        // left on it (Local, Utc) reflects the host, which is exactly what must not leak in.
        var wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        var written = wall.ToString("dd MMM yy HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();

        if (zone.IsInvalidTime(wall))
        {
            return new LocalTimeResolution(
                LocalTimeKind.Nonexistent, null, zone.Id, [],
                "AUD-020",
                $"{written} did not occur in {zone.Id}: clocks were advanced past it when daylight "
                + "saving time began. Check the entry against the form or the ledger. The "
                + "application will not substitute an adjacent time.");
        }

        if (zone.IsAmbiguousTime(wall))
        {
            var candidates = zone.GetAmbiguousTimeOffsets(wall)
                .OrderByDescending(o => o) // larger offset = daylight time = the EARLIER instant
                .Select(o => new DateTimeOffset(wall, o))
                .ToList();

            return choice switch
            {
                AmbiguousLocalTimeChoice.Earlier => Valid(candidates[0], zone, candidates),
                AmbiguousLocalTimeChoice.Later => Valid(candidates[^1], zone, candidates),
                _ => new LocalTimeResolution(
                    LocalTimeKind.Ambiguous, null, zone.Id, candidates,
                    "AUD-020",
                    $"{written} occurred twice in {zone.Id}: clocks were set back an hour when "
                    + "daylight saving time ended. State whether the entry means the first "
                    + "occurrence (still on daylight time) or the second (after the change).")
            };
        }

        return Valid(new DateTimeOffset(wall, zone.GetUtcOffset(wall)), zone, []);
    }

    /// <summary>An instant, shown as the room's wall clock with its offset.</summary>
    public static DateTimeOffset ToRoomLocal(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(instant, zone);
    }

    /// <summary>
    /// Resolves a room's configured zone id. The host must know the id natively: the solution is
    /// built with invariant globalization, so no Windows-to-IANA conversion is available at
    /// run time. An IIS host resolves Windows ids ("Eastern Standard Time"); a Linux host
    /// resolves IANA ids ("America/New_York"). An id the host does not know is a configuration
    /// error and is reported as one, never silently replaced by the host's own zone.
    /// </summary>
    public static TimeZoneInfo FindZone(string timeZoneId)
    {
        var id = Guard.NotBlank(timeZoneId, "AUD-020", "Evidence room time zone");

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new DomainRuleViolationException(
                "AUD-020",
                $"The evidence room's time zone '{id}' is not known to this host. Configure the "
                + "room with an identifier this host resolves (a Windows id such as \"Eastern "
                + "Standard Time\" on Windows; an IANA id such as \"America/New_York\" on Linux). "
                + "Local times cannot be recorded for the room until this is corrected.");
        }
    }

    private static LocalTimeResolution Valid(
        DateTimeOffset value, TimeZoneInfo zone, IReadOnlyList<DateTimeOffset> candidates)
        => new(LocalTimeKind.Valid, AccountabilityTime.Normalize(value), zone.Id, candidates, null, null);
}
