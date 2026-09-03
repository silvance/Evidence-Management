using Emc.Domain.Identity;

namespace Emc.Application.Abstractions;

/// <summary>
/// The authenticated user for the current request.
///
/// Role grants are resolved SERVER-SIDE, PER REQUEST, FROM THE DATABASE. No role information is
/// ever read from a cookie, form field, query string or hidden input (IAM-002). The identity
/// itself comes from Windows Authentication; this abstraction carries the resolved profile.
///
/// <see cref="IsAuthenticated"/> means "is a registered, active EMC user", NOT merely "presented
/// a valid Windows identity". A domain account that authenticates but has no EMC user record
/// holds no grants and can read nothing (IAM-017).
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    int UserId { get; }

    string DisplayName { get; }

    /// <summary>
    /// Active role grants, each naming the evidence room it applies to. A null room means a
    /// global grant, which only the administrator role may hold (IAM-016).
    /// </summary>
    IReadOnlyCollection<RoleGrant> Grants { get; }
}

public static class CurrentUserExtensions
{
    /// <summary>True when the user holds <paramref name="role"/> over the given evidence room.</summary>
    public static bool HasRole(this ICurrentUser user, string role, int? evidenceRoomId)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Grants.Any(g =>
            string.Equals(g.Role, role, StringComparison.Ordinal)
            && (g.EvidenceRoomId is null || g.EvidenceRoomId == evidenceRoomId));
    }

    /// <summary>Roles the user holds over <paramref name="evidenceRoomId"/>, including global grants.</summary>
    public static IReadOnlyCollection<string> RolesFor(this ICurrentUser user, int? evidenceRoomId)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Grants
            .Where(g => g.EvidenceRoomId is null || g.EvidenceRoomId == evidenceRoomId)
            .Select(g => g.Role)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Evidence rooms the user holds any room-scoped grant in. Used to scope list queries, so a
    /// listing never leaks the existence of records in a room the user cannot enter.
    /// </summary>
    public static IReadOnlyCollection<int> AccessibleEvidenceRoomIds(this ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Grants
            .Where(g => g.EvidenceRoomId is not null)
            .Select(g => g.EvidenceRoomId!.Value)
            .Distinct()
            .ToList();
    }
}
