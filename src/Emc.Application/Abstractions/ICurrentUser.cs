namespace Emc.Application.Abstractions;

/// <summary>
/// The authenticated user for the current request.
///
/// Roles are resolved SERVER-SIDE, PER REQUEST, FROM THE DATABASE. No role information is ever
/// read from a cookie, form field, query string or hidden input (IAM-002). The identity itself
/// comes from Windows Authentication; this abstraction carries the resolved profile.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    int UserId { get; }

    string DisplayName { get; }

    /// <summary>Resolved from the UserRoles table on this request. Never from the client.</summary>
    IReadOnlyCollection<string> Roles { get; }

    bool IsInRole(string role);
}
