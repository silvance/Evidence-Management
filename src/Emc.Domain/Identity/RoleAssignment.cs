using Emc.Domain.Common;

namespace Emc.Domain.Identity;

/// <summary>
/// A grant of a role to a user, normally scoped to a single evidence room.
///
/// Replaces the earlier global <c>UserRole</c>, which could not express evidence-room isolation:
/// a user granted <see cref="EmcRoles.Agent"/> anywhere held it everywhere.
///
/// AR 195-5 scopes operational authority to an evidence room throughout - the document-number
/// series runs per room (2-4c, 2-7g), custodians are appointed for a room (1-4g(1)), and
/// inspections and inventories are of a room (3-1, 3-2). Cross-room visibility is therefore
/// never implicit: it must be granted room by room.
///
/// Requirements: IAM-002, IAM-015, IAM-016.
/// </summary>
public class RoleAssignment : Entity
{
    private RoleAssignment() { }

    public RoleAssignment(
        int userId,
        int roleId,
        string roleName,
        int? evidenceRoomId,
        DateTimeOffset effectiveFrom,
        int grantedByUserId,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset? effectiveTo = null)
    {
        var role = Guard.NotBlank(roleName, "IAM-015", "Role name");

        // Only the administrator role may be held globally. Every operational role names an
        // evidence room, so authority cannot leak between rooms (IAM-016).
        if (evidenceRoomId is null && !EmcRoles.MayBeHeldGlobally.Contains(role))
        {
            throw new DomainRuleViolationException(
                "IAM-016",
                $"The {role} role carries operational authority over evidence and must be granted "
                + "for a specific evidence room. Only the Application Administrator role may be "
                + "held globally.");
        }

        if (evidenceRoomId is not null && EmcRoles.MayOnlyBeHeldGlobally.Contains(role))
        {
            throw new DomainRuleViolationException(
                "IAM-016",
                $"The {role} role administers the application as a whole and is granted globally, "
                + "not per evidence room.");
        }

        if (effectiveTo is not null && effectiveTo <= effectiveFrom)
        {
            throw new DomainRuleViolationException(
                "IAM-015", "A role assignment cannot end before it becomes effective.");
        }

        UserId = userId;
        RoleId = roleId;
        EvidenceRoomId = evidenceRoomId;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        GrantedByUserId = grantedByUserId;
        GrantedAtUtc = grantedAtUtc;
    }

    public int UserId { get; private set; }
    public User? User { get; private set; }

    public int RoleId { get; private set; }
    public Role? Role { get; private set; }

    /// <summary>Null only for a role in <see cref="EmcRoles.MayBeHeldGlobally"/>.</summary>
    public int? EvidenceRoomId { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }

    public int GrantedByUserId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }

    /// <summary>
    /// True when the grantor and the grantee are the same person. An administrator granting
    /// themselves a role cannot be prevented, so EMC makes it visible instead (IAM-010).
    /// </summary>
    public bool IsSelfGrant => UserId == GrantedByUserId;

    public bool IsActiveAt(DateTimeOffset at)
        => EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);

    /// <summary>True when this grant confers its role over <paramref name="evidenceRoomId"/>.</summary>
    public bool AppliesTo(int? evidenceRoomId)
        => EvidenceRoomId is null || EvidenceRoomId == evidenceRoomId;

    public void Revoke(DateTimeOffset effectiveTo)
    {
        if (effectiveTo < EffectiveFrom)
        {
            throw new DomainRuleViolationException(
                "IAM-015", "A role assignment cannot be revoked before it becomes effective.");
        }

        EffectiveTo = effectiveTo;
    }
}

/// <summary>
/// One role held by the current user, and the evidence room it applies to. Null
/// <paramref name="EvidenceRoomId"/> means a global grant.
/// </summary>
public sealed record RoleGrant(string Role, int? EvidenceRoomId);
