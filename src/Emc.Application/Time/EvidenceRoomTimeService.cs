using Emc.Application.Abstractions;
using Emc.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Time;

/// <summary>
/// The one place local evidence times are interpreted and displayed, in the EVIDENCE ROOM's
/// zone (EvidenceRoom.TimeZoneId). Pages and services go through this; none of them touch
/// TimeZoneInfo.Local or DateTime.Now.
/// </summary>
public interface IEvidenceRoomTimeService
{
    /// <summary>A wall-clock time written for this room, as an instant with the room's offset - or why not.</summary>
    Task<LocalTimeResolution> ResolveLocalAsync(
        int evidenceRoomId,
        DateTime wallClock,
        AmbiguousLocalTimeChoice choice = AmbiguousLocalTimeChoice.Unspecified,
        CancellationToken ct = default);

    /// <summary>An instant, as this room's wall clock.</summary>
    Task<DateTimeOffset> ToRoomLocalAsync(int evidenceRoomId, DateTimeOffset instant, CancellationToken ct = default);

    /// <summary>The application clock's now, as this room's wall clock. For form defaults.</summary>
    Task<DateTimeOffset> NowInRoomAsync(int evidenceRoomId, CancellationToken ct = default);
}

public sealed class EvidenceRoomTimeService : IEvidenceRoomTimeService
{
    private readonly IEmcDbContext _db;
    private readonly IClock _clock;
    private readonly Dictionary<int, TimeZoneInfo> _zones = [];

    public EvidenceRoomTimeService(IEmcDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<LocalTimeResolution> ResolveLocalAsync(
        int evidenceRoomId,
        DateTime wallClock,
        AmbiguousLocalTimeChoice choice = AmbiguousLocalTimeChoice.Unspecified,
        CancellationToken ct = default)
        => LocalTimeInterpretation.Interpret(wallClock, await ZoneAsync(evidenceRoomId, ct), choice);

    public async Task<DateTimeOffset> ToRoomLocalAsync(
        int evidenceRoomId, DateTimeOffset instant, CancellationToken ct = default)
        => LocalTimeInterpretation.ToRoomLocal(instant, await ZoneAsync(evidenceRoomId, ct));

    public Task<DateTimeOffset> NowInRoomAsync(int evidenceRoomId, CancellationToken ct = default)
        => ToRoomLocalAsync(evidenceRoomId, _clock.UtcNow, ct);

    private async Task<TimeZoneInfo> ZoneAsync(int evidenceRoomId, CancellationToken ct)
    {
        if (_zones.TryGetValue(evidenceRoomId, out var cached))
        {
            return cached;
        }

        var timeZoneId = await _db.EvidenceRooms
            .AsNoTracking()
            .Where(r => r.Id == evidenceRoomId)
            .Select(r => r.TimeZoneId)
            .FirstOrDefaultAsync(ct);

        if (timeZoneId is null)
        {
            throw new DomainRuleViolationException(
                "AUD-020", $"Evidence room {evidenceRoomId} was not found.");
        }

        var zone = LocalTimeInterpretation.FindZone(timeZoneId);
        _zones[evidenceRoomId] = zone;
        return zone;
    }
}
