using Emc.Domain.Common;
using Emc.Domain.Identity;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// Role grants. AR 195-5 scopes operational authority to an evidence room throughout - the
/// document-number series runs per room (2-4c, 2-7g), custodians are appointed for a room
/// (1-4g(1)), and inspections and inventories are of a room (3-1, 3-2). A grant that did not name
/// a room would hand a user authority over evidence the regulation never put in their charge.
///
/// Requirements: IAM-015, IAM-016. Invariant I-11.
/// </summary>
public class RoleAssignmentTests
{
    private const int RoomId = 7;
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static RoleAssignment New(
        string role,
        int? evidenceRoomId = RoomId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int userId = 100,
        int grantedByUserId = 900)
        => new(
            userId: userId,
            roleId: 1,
            roleName: role,
            evidenceRoomId: evidenceRoomId,
            effectiveFrom: from ?? Start,
            grantedByUserId: grantedByUserId,
            grantedAtUtc: from ?? Start,
            effectiveTo: to);

    [Fact]
    public void AnOperationalRoleCannotBeGrantedGlobally()
    {
        // IAM-016. The defect this prevents: a user granted Agent "everywhere" could read and act
        // on evidence in rooms they were never appointed to or assigned work in.
        foreach (var role in EmcRoles.All.Where(r => !EmcRoles.MayBeHeldGlobally.Contains(r)))
        {
            var error = Assert.Throws<DomainRuleViolationException>(
                () => New(role, evidenceRoomId: null));

            Assert.Equal("IAM-016", error.RequirementId);
        }
    }

    [Fact]
    public void TheAdministratorRoleCannotBeScopedToARoom()
    {
        // The rule cuts both ways. Administering the application is not an evidence-room activity,
        // and a room-scoped administrator grant would imply an authority over that room's
        // evidence that IAM-009 denies the administrator entirely.
        var error = Assert.Throws<DomainRuleViolationException>(
            () => New(EmcRoles.ApplicationAdministrator, evidenceRoomId: RoomId));

        Assert.Equal("IAM-016", error.RequirementId);
    }

    [Fact]
    public void TheAdministratorRoleIsGrantedGlobally()
    {
        var grant = New(EmcRoles.ApplicationAdministrator, evidenceRoomId: null);

        Assert.Null(grant.EvidenceRoomId);
        Assert.True(grant.AppliesTo(RoomId));
        Assert.True(grant.AppliesTo(null));
    }

    [Fact]
    public void ARoomScopedGrantAppliesOnlyToItsOwnRoom()
    {
        var grant = New(EmcRoles.Agent);

        Assert.True(grant.AppliesTo(RoomId));
        Assert.False(grant.AppliesTo(RoomId + 1));
    }

    [Fact]
    public void AGrantCannotEndBeforeItBegins()
    {
        var error = Assert.Throws<DomainRuleViolationException>(
            () => New(EmcRoles.Agent, to: Start.AddDays(-1)));

        Assert.Equal("IAM-015", error.RequirementId);
    }

    [Fact]
    public void AGrantIsInactiveOutsideItsEffectiveRange()
    {
        var grant = New(EmcRoles.Agent, from: Start, to: Start.AddDays(30));

        Assert.False(grant.IsActiveAt(Start.AddSeconds(-1)));
        Assert.True(grant.IsActiveAt(Start));
        Assert.True(grant.IsActiveAt(Start.AddDays(30).AddSeconds(-1)));

        // The end instant is exclusive: a grant that ends at T does not confer the role at T.
        Assert.False(grant.IsActiveAt(Start.AddDays(30)));
    }

    [Fact]
    public void ARevokedGrantConfersNothingAfterRevocation()
    {
        var grant = New(EmcRoles.Agent);
        grant.Revoke(Start.AddDays(10));

        Assert.True(grant.IsActiveAt(Start.AddDays(9)));
        Assert.False(grant.IsActiveAt(Start.AddDays(10)));
    }

    [Fact]
    public void AGrantCannotBeRevokedBeforeItBecomesEffective()
    {
        var grant = New(EmcRoles.Agent);

        var error = Assert.Throws<DomainRuleViolationException>(
            () => grant.Revoke(Start.AddDays(-1)));

        Assert.Equal("IAM-015", error.RequirementId);
    }

    [Fact]
    public void ASelfGrantIsVisible()
    {
        // IAM-010. An administrator granting themselves a role cannot be prevented in software -
        // they administer the software. EMC makes it attributable instead.
        var selfGrant = New(EmcRoles.Agent, userId: 100, grantedByUserId: 100);
        var normalGrant = New(EmcRoles.Agent, userId: 100, grantedByUserId: 900);

        Assert.True(selfGrant.IsSelfGrant);
        Assert.False(normalGrant.IsSelfGrant);
    }

    [Fact]
    public void ARoleNameIsRequired()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() => New("   "));

        Assert.Equal("IAM-015", error.RequirementId);
    }
}
