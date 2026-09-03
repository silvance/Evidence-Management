using System.Security.Claims;
using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Domain.Common;
using Emc.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Emc.Infrastructure.Persistence;

namespace Emc.Infrastructure.Services;

/// <summary>
/// The authenticated user for the current request.
///
/// IAM-002: roles are read from the DATABASE on each request. Nothing role-related is ever taken
/// from a cookie, a form field, a query string, a hidden input, or a claim the client could
/// influence. The only thing taken from the authentication ticket is the Windows identity, which
/// the operating system establishes and the client cannot forge.
/// </summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EmcDbContext _db;
    private readonly IClock _clock;
    private UserProfile? _resolved;
    private bool _resolutionAttempted;

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor, EmcDbContext db, IClock clock)
    {
        _httpContextAccessor = httpContextAccessor;
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// True only for a REGISTERED, ACTIVE EMC user - not merely a valid Windows principal. A
    /// domain account that authenticates but has no EMC record resolves to nothing and therefore
    /// holds no grants, so it can read nothing (IAM-017).
    /// </summary>
    public bool IsAuthenticated => Resolve() is not null;

    public int UserId => Resolve()?.Id ?? 0;

    public string DisplayName => Resolve()?.DisplayName ?? "(unregistered)";

    public IReadOnlyCollection<RoleGrant> Grants => Resolve()?.Grants ?? [];

    private UserProfile? Resolve()
    {
        if (_resolutionAttempted)
        {
            return _resolved;
        }

        _resolutionAttempted = true;

        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // The Windows account SID, established by the OS during Negotiate/Kerberos
        // authentication. Stable across account renames, and not client-supplied.
        var sid = principal.FindFirstValue(ClaimTypes.PrimarySid)
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        var user = _db.Users
            .AsNoTracking()
            .Where(u => u.ActiveDirectorySid == sid && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName })
            .FirstOrDefault();

        if (user is null)
        {
            return null;
        }

        // The application clock, so "is this grant in effect" is answered on the same clock as
        // every accountability timestamp and can be driven in tests.
        var now = _clock.UtcNow;

        // IAM-002: grants come from the database, per request, and carry the evidence room they
        // apply to. Nothing role-related is read from the client.
        var grants = (from assignment in _db.RoleAssignments.AsNoTracking()
                      join role in _db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                      where assignment.UserId == user.Id
                            && assignment.EffectiveFrom <= now
                            && (assignment.EffectiveTo == null || assignment.EffectiveTo > now)
                      select new RoleGrant(role.Name, assignment.EvidenceRoomId))
            .ToList();

        _resolved = new UserProfile(user.Id, user.DisplayName, grants);
        return _resolved;
    }

    private sealed record UserProfile(int Id, string DisplayName, IReadOnlyCollection<RoleGrant> Grants);
}

/// <summary>Per-request context for audit correlation. Never carries investigative content.</summary>
public sealed class HttpRequestContext : IRequestContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpRequestContext(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public string? SourceAddress
        => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? CorrelationId => _httpContextAccessor.HttpContext?.TraceIdentifier;
}
