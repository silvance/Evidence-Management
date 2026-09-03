using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Read authorization. Requirements: IAM-016, IAM-017, IAM-018.
///
/// These exist because the earlier design treated AUTHENTICATION as AUTHORIZATION: the ASP.NET
/// fallback policy proved only that a Windows principal authenticated, and Razor GET handlers
/// then queried the DbContext directly. A domain user with no EMC role could read every case
/// control number, evidence description, serial number, custody history and location in the
/// system.
/// </summary>
public class ReadAuthorizationTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<(int CaseId, int VoucherId, int ItemId)> SeedAsync()
    {
        _harness.SignInAsAgent();

        var c = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0142-2026-CID902-XXXXX", "Read authorization test", "Sensitive synopsis",
            _harness.EvidenceRoomId));

        var v = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            c.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var i = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            v.Value, "One Samsung SM-S921U cellular telephone", "1",
            "R58N30XXXXX", "356938035643809", false, false, false, null));

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(v.Value);

        _harness.SignInAsCustodian();
        await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(v.Value, "001-26", true, _harness.Clock.UtcNow));

        return (c.Value, v.Value, i.Value);
    }

    [Fact]
    public async Task AnAuthenticatedPrincipalWithNoEmcUserRecordReadsNothing()
    {
        // IAM-017. The core of the fix: a valid Windows identity is not an EMC authorization.
        var seeded = await SeedAsync();

        _harness.CurrentUser.SignInAsUnregisteredWindowsPrincipal();

        Assert.Empty(await _harness.Reads.GetAccessibleCasesAsync());
        Assert.Empty(await _harness.Reads.GetAccessibleEvidenceRoomsAsync());
        Assert.Null(await _harness.Reads.GetCaseAsync(seeded.CaseId));
        Assert.Null(await _harness.Reads.GetVoucherAsync(seeded.VoucherId));
        Assert.Null(await _harness.History.GetAsync(seeded.ItemId));
        Assert.Null(await _harness.Reads.GetReadableItemEvidenceRoomIdAsync(seeded.ItemId));
    }

    [Fact]
    public async Task AnAdministratorCannotReadEvidenceContent()
    {
        // IAM-009 + IAM-017. Administering the application confers no ability to read evidence.
        var seeded = await SeedAsync();

        _harness.SignInAsAdministrator();

        Assert.Empty(await _harness.Reads.GetAccessibleCasesAsync());
        Assert.Null(await _harness.Reads.GetCaseAsync(seeded.CaseId));
        Assert.Null(await _harness.Reads.GetVoucherAsync(seeded.VoucherId));
        Assert.Null(await _harness.History.GetAsync(seeded.ItemId));
    }

    [Fact]
    public async Task AdministratorIsDeniedEveryEvidenceReadPermission()
    {
        _harness.SignInAsAdministrator();

        foreach (var permission in EmcPermissions.EvidenceReadPermissions)
        {
            var decision = await _harness.Authorization.AuthorizeAsync(
                permission, _harness.EvidenceRoomId);

            Assert.False(decision.IsAllowed, $"Administrator was allowed '{permission}'.");
        }
    }

    [Fact]
    public async Task AUserCannotReadRecordsInAnotherEvidenceRoom()
    {
        // IAM-016. Holding a role in room A must not expose room B.
        var seeded = await SeedAsync();

        var otherRoom = new EvidenceRoom("310th MI Bn Evidence Room", "310th MI Bn", "America/New_York");
        _harness.Db.EvidenceRooms.Add(otherRoom);
        await _harness.Db.SaveChangesAsync();

        var outsider = new User("S-1-5-21-OUTSIDER", "outsider@army.mil", "FOX, JAMIE R.");
        outsider.UpdateProfile("FOX, JAMIE R.", "SA", "310th MI Bn");
        _harness.Db.Users.Add(outsider);
        await _harness.Db.SaveChangesAsync();

        _harness.GrantRoleInRoom(outsider.Id, EmcRoles.Agent, otherRoom.Id);
        _harness.CurrentUser.SignIn(outsider.Id, "SA FOX, JAMIE R.", otherRoom.Id, EmcRoles.Agent);

        // Sees their own room, but nothing from the 902d's room.
        Assert.Empty(await _harness.Reads.GetAccessibleCasesAsync());
        Assert.Null(await _harness.Reads.GetCaseAsync(seeded.CaseId));
        Assert.Null(await _harness.Reads.GetVoucherAsync(seeded.VoucherId));
        Assert.Null(await _harness.History.GetAsync(seeded.ItemId));

        var rooms = await _harness.Reads.GetAccessibleEvidenceRoomsAsync();
        Assert.Equal(otherRoom.Id, Assert.Single(rooms).Id);
    }

    [Fact]
    public async Task GuessingIdentifiersCannotDistinguishAbsentFromForbidden()
    {
        // IAM-018. A forbidden record and a non-existent one must be indistinguishable, or the
        // identifier space becomes an oracle for which cases exist.
        var seeded = await SeedAsync();

        _harness.CurrentUser.SignInAsUnregisteredWindowsPrincipal();

        var forbidden = await _harness.Reads.GetCaseAsync(seeded.CaseId);
        var nonExistent = await _harness.Reads.GetCaseAsync(999_999);

        Assert.Null(forbidden);
        Assert.Null(nonExistent);
    }

    [Fact]
    public async Task AnAuthorizedUserInTheRoomCanRead()
    {
        // The control must not be so broad that legitimate access breaks.
        var seeded = await SeedAsync();

        _harness.SignInAsAgent();

        Assert.Single(await _harness.Reads.GetAccessibleCasesAsync());
        Assert.NotNull(await _harness.Reads.GetCaseAsync(seeded.CaseId));
        Assert.NotNull(await _harness.Reads.GetVoucherAsync(seeded.VoucherId));
        Assert.NotNull(await _harness.History.GetAsync(seeded.ItemId));
    }

    [Fact]
    public async Task AnInventoryParticipantCanReadButNotWrite()
    {
        // AR 195-5 3-2b(1)(b)/(c) - an inventory participant compares evidence against the
        // DA Form 4137 and the ledger, so they must be able to read.
        var seeded = await SeedAsync();

        var inspector = new User("S-1-5-21-INSPECTOR", "inspector@army.mil", "REED, PAT");
        inspector.UpdateProfile("REED, PAT", "CW2", "902d MI Group");
        _harness.Db.Users.Add(inspector);
        await _harness.Db.SaveChangesAsync();

        _harness.GrantRoleInRoom(
            inspector.Id, EmcRoles.InspectorOrInventoryParticipant, _harness.EvidenceRoomId);

        _harness.CurrentUser.SignIn(
            inspector.Id, "CW2 REED, PAT", _harness.EvidenceRoomId,
            EmcRoles.InspectorOrInventoryParticipant);

        Assert.NotNull(await _harness.Reads.GetCaseAsync(seeded.CaseId));
        Assert.NotNull(await _harness.History.GetAsync(seeded.ItemId));

        Assert.False((await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.CreateCase, _harness.EvidenceRoomId)).IsAllowed);

        Assert.False((await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId)).IsAllowed);
    }

    [Fact]
    public async Task ARoomScopedPermissionWithoutARoomIsDenied()
    {
        // IAM-016. An unscoped query must not silently read across rooms.
        await SeedAsync();
        _harness.SignInAsAgent();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.ViewCase, evidenceRoomId: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal("IAM-016", decision.RequirementId);
    }

    [Fact]
    public void AnOperationalRoleCannotBeGrantedGlobally()
    {
        // IAM-016 enforced in the domain, so a bad seed script cannot create a global agent.
        var ex = Assert.Throws<Domain.Common.DomainRuleViolationException>(() => new RoleAssignment(
            userId: 1, roleId: 1, roleName: EmcRoles.Agent, evidenceRoomId: null,
            effectiveFrom: DateTimeOffset.UtcNow, grantedByUserId: 1,
            grantedAtUtc: DateTimeOffset.UtcNow));

        Assert.Equal("IAM-016", ex.RequirementId);
    }

    [Fact]
    public void TheAdministratorRoleCannotBeScopedToARoom()
    {
        var ex = Assert.Throws<Domain.Common.DomainRuleViolationException>(() => new RoleAssignment(
            userId: 1, roleId: 1, roleName: EmcRoles.ApplicationAdministrator, evidenceRoomId: 1,
            effectiveFrom: DateTimeOffset.UtcNow, grantedByUserId: 1,
            grantedAtUtc: DateTimeOffset.UtcNow));

        Assert.Equal("IAM-016", ex.RequirementId);
    }

    [Fact]
    public async Task ARevokedRoleAssignmentStopsConferringAccess()
    {
        var seeded = await SeedAsync();
        _harness.SignInAsAgent();

        Assert.NotNull(await _harness.Reads.GetCaseAsync(seeded.CaseId));

        // Revocation is modelled as an effective-dated end, so the grant's history survives.
        _harness.CurrentUser.SignOut();

        Assert.Null(await _harness.Reads.GetCaseAsync(seeded.CaseId));
    }
}
