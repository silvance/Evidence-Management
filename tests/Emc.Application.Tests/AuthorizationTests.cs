using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Domain.Common;
using Emc.Domain.Identity;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Authorization. Requirements: IAM-002, IAM-005, IAM-006, IAM-009, IAM-011, LOC-006.
/// </summary>
public class AuthorizationTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task AdministratorIsDeniedOnEveryAccountabilityPermission()
    {
        // IAM-009, invariant I-13. This is the assumption the system must NOT satisfy: that the
        // application administrator can do everything. AR 195-5 vests evidence authority in
        // appointed custodians (1-4g(1), 1-4h) and supervisors (1-4g(3)) - not in whoever
        // administers the software.
        //
        // Asserting over the whole permission set rather than a sample means a permission added
        // later is covered automatically.
        _harness.SignInAsAdministrator();

        foreach (var permission in EmcPermissions.AccountabilityPermissions)
        {
            var decision = await _harness.Authorization.AuthorizeAsync(
                permission, _harness.EvidenceRoomId);

            Assert.False(decision.IsAllowed, $"Administrator was allowed '{permission}'.");
        }
    }

    [Fact]
    public async Task TheAdministratorDenialExplainsItselfInRegulatoryTerms()
    {
        // A generic "access denied" would leave a maintainer wondering whether this is a bug. It
        // is not: it is the design.
        _harness.SignInAsAdministrator();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.False(decision.IsAllowed);
        Assert.Equal("IAM-009", decision.RequirementId);
        Assert.Contains("1-4g(1)", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministratorRetainsAdministrativePermissions()
    {
        // The boundary cuts one way only: the administrator administers the application.
        _harness.SignInAsAdministrator();

        foreach (var permission in new[]
        {
            EmcPermissions.ManageUsers,
            EmcPermissions.ManageRoles,
            EmcPermissions.ManageStorageLocations,
            EmcPermissions.ManageSystemConfiguration,
            EmcPermissions.VerifyIntegrity
        })
        {
            var decision = await _harness.Authorization.AuthorizeAsync(permission, _harness.EvidenceRoomId);
            Assert.True(decision.IsAllowed, $"Administrator was denied '{permission}'.");
        }
    }

    [Fact]
    public async Task ACustodianRoleWithoutAWrittenAppointmentIsDenied()
    {
        // IAM-005, invariant I-11. This is the heart of the authorization model: AR 195-5
        // 1-4g(1) requires custodians to be appointed IN WRITING, so the role alone confers
        // nothing. The harness deliberately gives this user the alternate-custodian role and no
        // appointment.
        _harness.SignInAsUnappointedCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.False(decision.IsAllowed);
        Assert.Equal("IAM-005", decision.RequirementId);
        Assert.Contains("1-4g(1)", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAppointedCustodianIsAllowed()
    {
        _harness.SignInAsCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public async Task AnAppointmentIsScopedToItsEvidenceRoom()
    {
        // AR 195-5 1-4g(1) appoints a custodian for an evidence room, and 2-4c runs the
        // document-number series per room. Authority does not travel between rooms (DEC-03).
        var otherRoom = new Domain.Storage.EvidenceRoom(
            "310th MI Bn Evidence Room", "310th MI Bn", "America/New_York");

        _harness.Db.EvidenceRooms.Add(otherRoom);
        await _harness.Db.SaveChangesAsync();

        _harness.SignInAsCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, otherRoom.Id);

        Assert.False(decision.IsAllowed);

        // Room scoping denies BEFORE the appointment check is reached: the custodian holds no
        // role grant in that room at all, so there is nothing to check an appointment against
        // (IAM-016, IAM-017). The earlier behaviour reached IAM-005, which was a weaker denial
        // because it presumed the role already applied there.
        Assert.Equal("IAM-017", decision.RequirementId);
    }

    [Fact]
    public async Task AnAgentCannotPerformCustodianActions()
    {
        // IAM-011. AR 195-5 2-4c makes assignment of the document number the CUSTODIAN's act; an
        // agent prepares the form (2-3b) but does not receive evidence into the evidence room.
        _harness.SignInAsAgent();

        foreach (var permission in new[]
        {
            EmcPermissions.AcceptEvidenceIntake,
            EmcPermissions.RecordOfficialDocumentNumber,
            EmcPermissions.AssignStorageLocation,
            EmcPermissions.PerformDisposition
        })
        {
            var decision = await _harness.Authorization.AuthorizeAsync(
                permission, _harness.EvidenceRoomId);

            Assert.False(decision.IsAllowed, $"Agent was allowed '{permission}'.");
        }
    }

    [Fact]
    public async Task AnAgentCannotRecordTheDocumentNumberThroughTheService()
    {
        // The same rule at the use-case boundary, not just the policy table - and the denial is
        // audit logged (IAM-011, IAM-001).
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0200-2026-CID902-XXXXX", "Agent boundary test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One item", "1", null, null, false, false, false, null));

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        var result = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherResult.Value, "001-26", true, _harness.Clock.UtcNow));

        Assert.False(result.Succeeded);

        var denial = _harness.Db.AuditEvents
            .Where(a => a.EventType == AuditEventType.PermissionDenied)
            .ToList();

        Assert.NotEmpty(denial);
    }

    [Fact]
    public async Task AnUnauthenticatedUserIsDenied()
    {
        _harness.CurrentUser.SignOut();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.CreateCase, _harness.EvidenceRoomId);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task AnAppointedAlternateWithNoOpenAbsenceCannotActAsCustodian()
    {
        // IAM-006 / IAM-019. THE regulatory correction: AR 195-5 para 1-4i has the alternate
        // assume the primary's duties "during his or her temporary absence". An alternate can
        // hold that appointment for months without the primary ever being absent, so the
        // appointment alone confers no custodial authority.
        //
        // The earlier model allowed the appointed alternate to act as evidence custodian every
        // day, which is not what the regulation says.
        _harness.AppointAlternate(_harness.AlternateCustodianUserId, _harness.Clock.UtcNow.AddDays(-180));

        _harness.SignInAsUnappointedCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.False(decision.IsAllowed);
        Assert.Equal("IAM-006", decision.RequirementId);
        Assert.Contains("1-4i", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlternateWhoHasAssumedDutiesIsAuthorized()
    {
        var appointment = _harness.AppointAlternate(
            _harness.AlternateCustodianUserId, _harness.Clock.UtcNow.AddDays(-180));

        _harness.AssumeDuties(appointment, _harness.Clock.UtcNow.AddDays(-2));
        _harness.SignInAsUnappointedCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.True(decision.IsAllowed, decision.Reason);
    }

    [Fact]
    public async Task AlternateAuthorityCeasesWhenThePrimaryResumes()
    {
        // AR 195-5 1-7c(2) - the primary resumes and signs the ledger statement.
        var appointment = _harness.AppointAlternate(
            _harness.AlternateCustodianUserId, _harness.Clock.UtcNow.AddDays(-180));

        var assumption = _harness.AssumeDuties(appointment, _harness.Clock.UtcNow.AddDays(-5));

        assumption.RecordPrimaryResumption(
            _harness.Clock.UtcNow.AddDays(-1),
            "I BAKER, ALICE C., resume my position as primary evidence custodian.",
            _harness.CommanderUserId,
            _harness.Clock.UtcNow.AddDays(-1));

        await _harness.Db.SaveChangesAsync();

        _harness.SignInAsUnappointedCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.False(decision.IsAllowed);
        Assert.Equal("IAM-006", decision.RequirementId);
    }

    [Fact]
    public async Task TheThirtyDayLimitRunsFromAssumptionOfDutiesNotAppointment()
    {
        // IAM-019. Appointed 180 days ago, assumed duties 40 days ago: warned because the
        // ACTING period exceeded 30 days, not because the appointment is old.
        var appointment = _harness.AppointAlternate(
            _harness.AlternateCustodianUserId, _harness.Clock.UtcNow.AddDays(-180));

        _harness.AssumeDuties(appointment, _harness.Clock.UtcNow.AddDays(-40));
        _harness.SignInAsUnappointedCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        // Warned, not blocked: late orders must not halt evidence intake, and the warning is
        // visible at the next inspection (DEC-05).
        Assert.True(decision.IsAllowed);
        Assert.NotNull(decision.Warnings);
        Assert.Contains(decision.Warnings!, w => w.Contains("1-4i", StringComparison.Ordinal));
        Assert.Contains(decision.Warnings!, w => w.Contains("3-2d", StringComparison.Ordinal));
        Assert.Contains(decision.Warnings!, w => w.Contains("40 consecutive days", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAlternateWithinThirtyDaysIsNotWarned()
    {
        var appointment = _harness.AppointAlternate(
            _harness.AlternateCustodianUserId, _harness.Clock.UtcNow.AddDays(-180));

        _harness.AssumeDuties(appointment, _harness.Clock.UtcNow.AddDays(-10));
        _harness.SignInAsUnappointedCustodian();

        var decision = await _harness.Authorization.AuthorizeAsync(
            EmcPermissions.AcceptEvidenceIntake, _harness.EvidenceRoomId);

        Assert.True(decision.IsAllowed, decision.Reason);
        Assert.Empty(decision.Warnings ?? []);
    }

    [Fact]
    public void RolePermissionMap_GivesTheAdministratorNoAccountabilityPermission()
    {
        // The same guarantee asserted directly against the policy table, so that a change to the
        // map is caught even if the authorization service is refactored.
        var administratorPermissions = RolePermissionMap.PermissionsFor(
            EmcRoles.ApplicationAdministrator);

        Assert.Empty(administratorPermissions.Intersect(EmcPermissions.AccountabilityPermissions));
    }

    [Fact]
    public void EveryAccountabilityPermissionIsHeldBySomeRealRole()
    {
        // Guards against the opposite failure: a permission that no role holds is an operation
        // nobody can perform, which would be an unreachable feature rather than a security
        // control.
        var granting = EmcRoles.All
            .Where(r => r != EmcRoles.ApplicationAdministrator)
            .SelectMany(RolePermissionMap.PermissionsFor)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var permission in EmcPermissions.AccountabilityPermissions)
        {
            Assert.True(granting.Contains(permission), $"No role grants '{permission}'.");
        }
    }
}
