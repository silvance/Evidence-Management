using System.Security.Claims;
using Emc.Application.Abstractions;
using Emc.Application.Audit;
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
    private UserProfile? _resolved;

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor, EmcDbContext db)
    {
        _httpContextAccessor = httpContextAccessor;
        _db = db;
    }

    public bool IsAuthenticated => Resolve() is not null;

    public int UserId => Resolve()?.Id ?? 0;

    public string DisplayName => Resolve()?.DisplayName ?? "(unauthenticated)";

    public IReadOnlyCollection<string> Roles => Resolve()?.Roles ?? [];

    public bool IsInRole(string role) => Roles.Contains(role);

    private UserProfile? Resolve()
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

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

        var roles = (from userRole in _db.UserRoles.AsNoTracking()
                     join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                     where userRole.UserId == user.Id
                     select role.Name)
            .ToList();

        _resolved = new UserProfile(user.Id, user.DisplayName, roles);
        return _resolved;
    }

    private sealed record UserProfile(int Id, string DisplayName, IReadOnlyCollection<string> Roles);
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
