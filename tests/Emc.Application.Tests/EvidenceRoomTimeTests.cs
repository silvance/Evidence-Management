using Emc.Application.Time;
using Emc.Domain.Common;
using Emc.Domain.Storage;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Local evidence times are interpreted in the EVIDENCE ROOM's zone, never the host's, and the
/// daylight-saving edge cases are explicit. Requirements: AUD-011, AUD-019, AUD-020.
///
/// 2026 in the United States: clocks spring forward 08 MAR 02:00 -> 03:00 and fall back
/// 01 NOV 02:00 -> 01:00. Europe: 29 MAR and 25 OCT.
/// </summary>
public class EvidenceRoomTimeTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static TimeZoneInfo Zone(string id) => LocalTimeInterpretation.FindZone(id);

    [Theory]
    [InlineData("America/New_York", 2026, 9, 3, 9, 15, -4)]   // EDT
    [InlineData("America/New_York", 2026, 1, 15, 9, 15, -5)]  // EST
    [InlineData("America/Chicago", 2026, 9, 3, 9, 15, -5)]    // CDT
    [InlineData("America/Chicago", 2026, 12, 3, 9, 15, -6)]   // CST
    [InlineData("Europe/Berlin", 2026, 9, 3, 9, 15, 2)]       // CEST
    [InlineData("Asia/Tokyo", 2026, 9, 3, 9, 15, 9)]          // no DST
    public void AWallClockTimeIsInterpretedInTheRoomsZone(
        string zoneId, int y, int mo, int d, int h, int mi, int expectedOffsetHours)
    {
        // AUD-011. "03 SEP 26 09:15" written in a Chicago evidence room is 09:15 Chicago time,
        // whatever zone the server runs in.
        var result = LocalTimeInterpretation.Interpret(new DateTime(y, mo, d, h, mi, 0), Zone(zoneId));

        Assert.Equal(LocalTimeKind.Valid, result.Kind);
        Assert.True(result.Succeeded);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), result.Value!.Value.DateTime);
        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), result.Value.Value.Offset);
    }

    [Fact]
    public void TheHostsZoneDoesNotEnterIntoIt()
    {
        // AUD-019. The regression for TimeZoneInfo.Local. The same entry is interpreted
        // identically whether the process's local zone is UTC, Tokyo or Honolulu.
        var wall = new DateTime(2026, 9, 3, 9, 15, 0);
        var chicago = Zone("America/Chicago");
        var expected = LocalTimeInterpretation.Interpret(wall, chicago).Value;

        var original = Environment.GetEnvironmentVariable("TZ");
        try
        {
            foreach (var hostZone in new[] { "Asia/Tokyo", "Pacific/Honolulu", "Etc/UTC" })
            {
                Environment.SetEnvironmentVariable("TZ", hostZone);
                TimeZoneInfo.ClearCachedData();

                Assert.Equal(expected, LocalTimeInterpretation.Interpret(wall, chicago).Value);
                Assert.Equal(TimeSpan.FromHours(-5), expected!.Value.Offset);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", original);
            TimeZoneInfo.ClearCachedData();
        }
    }

    [Fact]
    public void ATimeInTheRepeatedHourIsAmbiguousAndIsNotResolvedByDefault()
    {
        // AUD-020. 01 NOV 26 01:30 in New York happened twice: 01:30 EDT (05:30Z) and, an hour
        // later, 01:30 EST (06:30Z). Which one the custodian meant is a fact only they know.
        var wall = new DateTime(2026, 11, 1, 1, 30, 0);
        var result = LocalTimeInterpretation.Interpret(wall, Zone("America/New_York"));

        Assert.Equal(LocalTimeKind.Ambiguous, result.Kind);
        Assert.False(result.Succeeded);
        Assert.Equal("AUD-020", result.RequirementId);
        Assert.Contains("twice", result.Error!, StringComparison.Ordinal);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(TimeSpan.FromHours(-4), result.Candidates[0].Offset);
        Assert.Equal(TimeSpan.FromHours(-5), result.Candidates[1].Offset);
        Assert.Equal(TimeSpan.FromHours(1), result.Candidates[1] - result.Candidates[0]);
    }

    [Theory]
    [InlineData(AmbiguousLocalTimeChoice.Earlier, -4)]
    [InlineData(AmbiguousLocalTimeChoice.Later, -5)]
    public void AnAmbiguousTimeIsResolvedByTheStatedChoice(AmbiguousLocalTimeChoice choice, int offsetHours)
    {
        var wall = new DateTime(2026, 11, 1, 1, 30, 0);
        var result = LocalTimeInterpretation.Interpret(wall, Zone("America/New_York"), choice);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(wall, result.Value!.Value.DateTime);
        Assert.Equal(TimeSpan.FromHours(offsetHours), result.Value.Value.Offset);
    }

    [Fact]
    public void ATimeInTheSkippedHourIsNonexistentAndIsRefused()
    {
        // AUD-020. 08 MAR 26 02:30 in Chicago never happened; clocks went from 02:00 to 03:00.
        // Substituting 03:30 or 01:30 would be inventing when something occurred.
        var result = LocalTimeInterpretation.Interpret(
            new DateTime(2026, 3, 8, 2, 30, 0), Zone("America/Chicago"), AmbiguousLocalTimeChoice.Earlier);

        Assert.Equal(LocalTimeKind.Nonexistent, result.Kind);
        Assert.False(result.Succeeded);
        Assert.Equal("AUD-020", result.RequirementId);
        Assert.Contains("did not occur", result.Error!, StringComparison.Ordinal);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void TheChoiceIsIgnoredForAnUnambiguousTime()
    {
        var result = LocalTimeInterpretation.Interpret(
            new DateTime(2026, 9, 3, 9, 15, 0), Zone("America/New_York"), AmbiguousLocalTimeChoice.Later);

        Assert.Equal(LocalTimeKind.Valid, result.Kind);
        Assert.Equal(TimeSpan.FromHours(-4), result.Value!.Value.Offset);
    }

    [Fact]
    public void AHostKindOnTheInputIsDiscarded()
    {
        // A DateTime that arrives marked Local or Utc carries the HOST's idea of itself. The
        // wall-clock digits are what was written; the kind is not.
        var zone = Zone("America/Chicago");
        var wall = new DateTime(2026, 9, 3, 9, 15, 0);

        var unspecified = LocalTimeInterpretation.Interpret(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone).Value;
        var local = LocalTimeInterpretation.Interpret(DateTime.SpecifyKind(wall, DateTimeKind.Local), zone).Value;
        var utc = LocalTimeInterpretation.Interpret(DateTime.SpecifyKind(wall, DateTimeKind.Utc), zone).Value;

        Assert.Equal(unspecified, local);
        Assert.Equal(unspecified, utc);
    }

    [Fact]
    public void AnInstantDisplaysAsTheRoomsWallClock()
    {
        // 13:15Z on 03 SEP 26 is 09:15 in New York and 08:15 in Chicago.
        var instant = new DateTimeOffset(2026, 9, 3, 13, 15, 0, TimeSpan.Zero);

        var newYork = LocalTimeInterpretation.ToRoomLocal(instant, Zone("America/New_York"));
        var chicago = LocalTimeInterpretation.ToRoomLocal(instant, Zone("America/Chicago"));

        Assert.Equal(new DateTime(2026, 9, 3, 9, 15, 0), newYork.DateTime);
        Assert.Equal(new DateTime(2026, 9, 3, 8, 15, 0), chicago.DateTime);
        Assert.Equal(instant, newYork);
        Assert.Equal(instant, chicago);
    }

    [Fact]
    public void AnUnknownZoneIdIsAConfigurationErrorNotAFallbackToTheHost()
    {
        // The solution runs with invariant globalization, so no Windows/IANA conversion exists
        // at run time; a room must be configured with an id the host knows. When it is not, the
        // failure is explicit. Silently using the host's zone would be the original defect.
        var ex = Assert.Throws<DomainRuleViolationException>(
            () => LocalTimeInterpretation.FindZone("Nowhere/Imaginary"));

        Assert.Equal("AUD-020", ex.RequirementId);
        Assert.Contains("Nowhere/Imaginary", ex.Message, StringComparison.Ordinal);

        Assert.Throws<DomainRuleViolationException>(() => LocalTimeInterpretation.FindZone("  "));
    }

    [Fact]
    public async Task TheServiceUsesEachRoomsOwnZone()
    {
        // Two rooms in two zones. The same wall-clock entry is a different instant in each, and
        // "now" reads differently in each - all from the application clock.
        var chicagoRoom = new EvidenceRoom("Fort Sam Houston Evidence Room", "470th MI Bde", "America/Chicago");
        _harness.Db.EvidenceRooms.Add(chicagoRoom);
        await _harness.Db.SaveChangesAsync();

        var service = new EvidenceRoomTimeService(_harness.Db, _harness.Clock);
        var wall = new DateTime(2026, 9, 3, 9, 15, 0);

        var newYork = await service.ResolveLocalAsync(_harness.EvidenceRoomId, wall);
        var chicago = await service.ResolveLocalAsync(chicagoRoom.Id, wall);

        Assert.Equal(TimeSpan.FromHours(-4), newYork.Value!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(-5), chicago.Value!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(1), chicago.Value.Value - newYork.Value.Value);

        var nowNewYork = await service.NowInRoomAsync(_harness.EvidenceRoomId);
        var nowChicago = await service.NowInRoomAsync(chicagoRoom.Id);

        Assert.Equal(_harness.Clock.UtcNow, nowNewYork);
        Assert.Equal(_harness.Clock.UtcNow, nowChicago);
        Assert.Equal(TimeSpan.FromHours(1), nowNewYork.DateTime - nowChicago.DateTime);
    }

    [Fact]
    public async Task AMissingRoomIsRefused()
    {
        var service = new EvidenceRoomTimeService(_harness.Db, _harness.Clock);

        var ex = await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => service.ResolveLocalAsync(999_999, new DateTime(2026, 9, 3, 9, 15, 0)));

        Assert.Equal("AUD-020", ex.RequirementId);
    }
}
