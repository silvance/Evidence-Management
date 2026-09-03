using Emc.Domain.Common;

namespace Emc.Domain.Identity;

/// <summary>
/// An application user.
///
/// EMC stores NO passwords and NO password hashes (IAM-003). Authentication is Windows
/// Authentication (Negotiate/Kerberos), which in an Army environment is CAC-backed. This record
/// is a profile and authorization anchor keyed to the Active Directory object SID, which is
/// stable across account renames. A credential store here would be a liability to defend,
/// rotate and audit for no benefit (docs/architecture.md §8).
/// </summary>
public class User : Entity, IConcurrencyStamped
{
    private readonly List<UserRole> _roles = [];

    private User() { }

    public User(string activeDirectorySid, string userPrincipalName, string displayName)
    {
        ActiveDirectorySid = Guard.NotBlank(activeDirectorySid, "IAM-003", "Active Directory SID");
        UserPrincipalName = Guard.NotBlank(userPrincipalName, "IAM-003", "User principal name");
        DisplayName = Guard.NotBlank(displayName, "IAM-003", "Display name");
        IsActive = true;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>Stable AD identifier. Survives account renames; the authoritative key.</summary>
    public string ActiveDirectorySid { get; private set; } = string.Empty;

    public string UserPrincipalName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>Rank, grade or title, as it appears on printed certifications (AR 195-5 3-1b(2)).</summary>
    public string? RankOrGrade { get; private set; }

    public string? OrganizationOrUnit { get; private set; }
    public bool IsActive { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public void UpdateProfile(string displayName, string? rankOrGrade, string? organizationOrUnit)
    {
        DisplayName = Guard.NotBlank(displayName, "IAM-003", "Display name");
        RankOrGrade = Guard.TrimToNull(rankOrGrade);
        OrganizationOrUnit = Guard.TrimToNull(organizationOrUnit);
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>Printed name and grade, in the form AR 195-5 3-1b(2) and 3-2g(1) prescribe.</summary>
    public string PrintedNameAndGrade =>
        string.IsNullOrWhiteSpace(RankOrGrade) ? DisplayName : $"{RankOrGrade} {DisplayName}";
}

public class Role : Entity
{
    private Role() { }

    public Role(string name, string description)
    {
        Name = Guard.NotBlank(name, "IAM-002", "Role name");
        Description = Guard.NotBlank(description, "IAM-002", "Role description");
    }

    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
}

public class UserRole : Entity
{
    private UserRole() { }

    public UserRole(int userId, int roleId, int grantedByUserId, DateTimeOffset grantedAtUtc)
    {
        UserId = userId;
        RoleId = roleId;
        GrantedByUserId = grantedByUserId;
        GrantedAtUtc = grantedAtUtc;
    }

    public int UserId { get; private set; }
    public User? User { get; private set; }

    public int RoleId { get; private set; }
    public Role? Role { get; private set; }

    public int GrantedByUserId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }

    /// <summary>
    /// True when the grantor and the grantee are the same person. An administrator granting
    /// themselves a custodian role cannot be prevented, so EMC makes it visible instead
    /// (IAM-010). Surfaced in the audit log and on the administration screens.
    /// </summary>
    public bool IsSelfGrant => UserId == GrantedByUserId;
}
