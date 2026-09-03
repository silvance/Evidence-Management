using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Items;
using Emc.Domain.Common;
using Emc.Domain.Configuration;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Emc.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Tests;

/// <summary>
/// A running slice of the application over SQLite in-memory.
///
/// SQLite rather than the EF in-memory provider because the tests need REAL relational semantics:
/// foreign keys, unique and filtered indexes, and transactions. The in-memory provider enforces
/// none of those, so it would pass tests that production would fail - which for an
/// accountability system is the worst kind of green.
/// </summary>
public sealed class SliceTestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public SliceTestHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // SQLite does not enforce foreign keys unless asked.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<EmcDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new EmcDbContext(options);
        Db.Database.EnsureCreated();

        Clock = new TestClock(new DateTimeOffset(2026, 9, 3, 13, 15, 0, TimeSpan.Zero));
        CurrentUser = new TestCurrentUser();

        Seed();
    }

    public EmcDbContext Db { get; }
    public TestClock Clock { get; }
    public TestCurrentUser CurrentUser { get; }

    public int EvidenceRoomId { get; private set; }
    public int ShelfBBin14Id { get; private set; }
    public int HighValueSafeId { get; private set; }
    public int AgentUserId { get; private set; }
    public int CustodianUserId { get; private set; }
    public int AlternateCustodianUserId { get; private set; }
    public int AdministratorUserId { get; private set; }
    public int CommanderUserId { get; private set; }

    public IEvidenceAuthorizationService Authorization
        => new EvidenceAuthorizationService(Db, CurrentUser, Clock);

    public IAuditRecorder Audit => new AuditRecorder(Db, CurrentUser, Clock, new TestRequestContext());

    public IItemEventRecorder EventRecorder => new ItemEventRecorder(Db);

    public ICaseService Cases => new CaseService(Db, Authorization, CurrentUser, Audit, Clock);

    public IVoucherService Vouchers
        => new VoucherService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    public IEvidenceIntakeService Intake
        => new EvidenceIntakeService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    public Reads.IEvidenceReadService Reads
        => new Reads.EvidenceReadService(Db, Authorization, CurrentUser);

    public IItemHistoryService History
        => new ItemHistoryService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    /// <summary>Signs in as an agent in this harness's evidence room (AR 195-5 2-3b).</summary>
    public void SignInAsAgent()
        => CurrentUser.SignIn(AgentUserId, "SA SMITH, JOHN A.", EvidenceRoomId, EmcRoles.Agent);

    /// <summary>Signs in as the appointed primary evidence custodian (AR 195-5 1-4g(1), 1-4h).</summary>
    public void SignInAsCustodian()
        => CurrentUser.SignIn(
            CustodianUserId, "SA BAKER, ALICE C.", EvidenceRoomId, EmcRoles.PrimaryEvidenceCustodian);

    /// <summary>
    /// Signs in as a user holding the custodian ROLE but with no written appointment - the case
    /// AR 195-5 1-4g(1) makes decisive (IAM-005).
    /// </summary>
    public void SignInAsUnappointedCustodian()
        => CurrentUser.SignIn(
            AlternateCustodianUserId, "SA CHEN, DAVID L.", EvidenceRoomId,
            EmcRoles.AlternateEvidenceCustodian);

    /// <summary>Signs in as an application administrator - a GLOBAL grant (IAM-009, IAM-016).</summary>
    public void SignInAsAdministrator()
        => CurrentUser.SignInGlobal(
            AdministratorUserId, "MR. DOE, ROBERT", EmcRoles.ApplicationAdministrator);

    public void SignInAsCommander()
        => CurrentUser.SignIn(
            CommanderUserId, "MAJ EVANS, SARAH", EvidenceRoomId, EmcRoles.CommanderOrSac);

    private void Seed()
    {
        var room = new EvidenceRoom("902d MI Group Evidence Room", "902d MI Group", "America/New_York");
        Db.EvidenceRooms.Add(room);

        Db.SystemConfigurations.Add(new SystemConfiguration("902d MI Group", "UNCLASSIFIED"));

        foreach (var name in EmcRoles.All)
        {
            Db.Roles.Add(new Role(name, $"{name} role"));
        }

        var agent = NewUser("SMITH, JOHN A.", "SA");
        var custodian = NewUser("BAKER, ALICE C.", "SA");
        var alternate = NewUser("CHEN, DAVID L.", "SA");
        var administrator = NewUser("DOE, ROBERT", "MR.");
        var commander = NewUser("EVANS, SARAH", "MAJ");

        Db.Users.AddRange(agent, custodian, alternate, administrator, commander);
        Db.SaveChanges();

        EvidenceRoomId = room.Id;
        AgentUserId = agent.Id;
        CustodianUserId = custodian.Id;
        AlternateCustodianUserId = alternate.Id;
        AdministratorUserId = administrator.Id;
        CommanderUserId = commander.Id;

        GrantRoleInRoom(agent.Id, EmcRoles.Agent, room.Id);
        GrantRoleInRoom(custodian.Id, EmcRoles.PrimaryEvidenceCustodian, room.Id);
        GrantRoleInRoom(alternate.Id, EmcRoles.AlternateEvidenceCustodian, room.Id);
        GrantRoleInRoom(commander.Id, EmcRoles.CommanderOrSac, room.Id);

        // IAM-016 - the administrator is the only role held globally, and it carries no
        // authority over evidence.
        GrantRoleGlobally(administrator.Id, EmcRoles.ApplicationAdministrator);

        // AR 195-5 1-4g(1) - the primary custodian is appointed IN WRITING. Only the appointed
        // custodian holds evidence-room authority; the alternate above is deliberately left
        // unappointed so the tests can prove that the role alone is not enough (IAM-005).
        Db.CustodianAppointments.Add(new CustodianAppointment(
            evidenceRoomId: room.Id,
            userId: custodian.Id,
            appointmentType: CustodianAppointmentType.Primary,
            personnelCategory: PersonnelCategory.MilitaryCi,
            effectiveFrom: Clock.UtcNow.AddDays(-30),
            appointmentOrderReference: "ORDERS 2026-114, 902d MI Group",
            appointingAuthority: "Commander, 902d MI Group",
            eligibilityAttested: true,
            recordedByUserId: commander.Id,
            recordedAtUtc: Clock.UtcNow.AddDays(-30)));

        var shelfB = new StorageLocation(room.Id, "Shelf B", StorageLocationKind.Shelf);
        Db.StorageLocations.Add(shelfB);
        Db.SaveChanges();

        var bin14 = new StorageLocation(room.Id, "Bin 14", StorageLocationKind.Bin, shelfB);
        var safe = new StorageLocation(
            room.Id, "High-Value Safe / Drawer 2", StorageLocationKind.HighValueContainer);

        Db.StorageLocations.AddRange(bin14, safe);
        Db.SaveChanges();

        ShelfBBin14Id = bin14.Id;
        HighValueSafeId = safe.Id;
    }

    private static User NewUser(string name, string grade)
    {
        var user = new User($"S-1-5-21-{Guid.NewGuid():N}", $"{name.Replace(", ", ".", StringComparison.Ordinal)}@army.mil", name);
        user.UpdateProfile(name, grade, "902d MI Group");
        return user;
    }

    /// <summary>
    /// AR 195-5 1-4g(1) - records a written alternate appointment. Note this does NOT authorize
    /// acting as the evidence custodian; that needs a duty assumption (IAM-006, IAM-019).
    /// </summary>
    public CustodianAppointment AppointAlternate(int userId, DateTimeOffset? from = null)
    {
        var appointment = new CustodianAppointment(
            evidenceRoomId: EvidenceRoomId,
            userId: userId,
            appointmentType: CustodianAppointmentType.Alternate,
            personnelCategory: PersonnelCategory.MilitaryCi,
            effectiveFrom: from ?? Clock.UtcNow.AddDays(-180),
            appointmentOrderReference: "ORDERS 2026-118, 902d MI Group",
            appointingAuthority: "Commander, 902d MI Group",
            eligibilityAttested: true,
            recordedByUserId: CommanderUserId,
            recordedAtUtc: from ?? Clock.UtcNow.AddDays(-180));

        Db.CustodianAppointments.Add(appointment);
        Db.SaveChanges();
        return appointment;
    }

    /// <summary>AR 195-5 1-4i / 1-7c(1) - the alternate actually assumes the primary's duties.</summary>
    public CustodianDutyAssumption AssumeDuties(
        CustodianAppointment alternateAppointment, DateTimeOffset? assumedAt = null)
    {
        var primary = Db.CustodianAppointments.First(
            a => a.EvidenceRoomId == EvidenceRoomId
                 && a.AppointmentType == CustodianAppointmentType.Primary);

        var at = assumedAt ?? Clock.UtcNow;

        var assumption = new CustodianDutyAssumption(
            evidenceRoomId: EvidenceRoomId,
            primaryAppointmentId: primary.Id,
            alternateAppointmentId: alternateAppointment.Id,
            alternateUserId: alternateAppointment.UserId,
            primaryAbsenceStart: at,
            alternateAssumedDutiesAt: at,
            assumptionLedgerAttestation:
                "I CHEN, DAVID L., assume all duties of the primary evidence custodian during the "
                + "temporary absence of the regularly appointed custodian.",
            recordedByUserId: CommanderUserId,
            recordedAtUtc: at);

        Db.CustodianDutyAssumptions.Add(assumption);
        Db.SaveChanges();
        return assumption;
    }

    public void GrantRoleInRoom(int userId, string roleName, int evidenceRoomId)
    {
        var role = Db.Roles.Single(r => r.Name == roleName);

        Db.RoleAssignments.Add(new RoleAssignment(
            userId, role.Id, roleName, evidenceRoomId,
            Clock.UtcNow.AddDays(-60), userId, Clock.UtcNow.AddDays(-60)));

        Db.SaveChanges();
    }

    private void GrantRoleGlobally(int userId, string roleName)
    {
        var role = Db.Roles.Single(r => r.Name == roleName);

        Db.RoleAssignments.Add(new RoleAssignment(
            userId, role.Id, roleName, null,
            Clock.UtcNow.AddDays(-60), userId, Clock.UtcNow.AddDays(-60)));

        Db.SaveChanges();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

public sealed class TestCurrentUser : ICurrentUser
{
    private readonly List<RoleGrant> _grants = [];

    public bool IsAuthenticated { get; private set; }
    public int UserId { get; private set; }
    public string DisplayName { get; private set; } = "(unregistered)";
    public IReadOnlyCollection<RoleGrant> Grants => _grants;

    /// <summary>Signs in with roles scoped to one evidence room.</summary>
    public void SignIn(int userId, string displayName, int evidenceRoomId, params string[] roles)
    {
        IsAuthenticated = true;
        UserId = userId;
        DisplayName = displayName;
        _grants.Clear();
        _grants.AddRange(roles.Select(r => new RoleGrant(r, evidenceRoomId)));
    }

    /// <summary>Signs in with global grants - only valid for the administrator role.</summary>
    public void SignInGlobal(int userId, string displayName, params string[] roles)
    {
        IsAuthenticated = true;
        UserId = userId;
        DisplayName = displayName;
        _grants.Clear();
        _grants.AddRange(roles.Select(r => new RoleGrant(r, null)));
    }

    /// <summary>
    /// A valid Windows principal with no EMC user record. IsAuthenticated is false because
    /// authentication is not registration, and no grants exist (IAM-017).
    /// </summary>
    public void SignInAsUnregisteredWindowsPrincipal()
    {
        IsAuthenticated = false;
        UserId = 0;
        DisplayName = "(unregistered)";
        _grants.Clear();
    }

    public void SignOut()
    {
        IsAuthenticated = false;
        UserId = 0;
        DisplayName = "(unregistered)";
        _grants.Clear();
    }
}

public sealed class TestRequestContext : IRequestContext
{
    public string? SourceAddress => "10.0.0.5";
    public string? CorrelationId => "TEST-CORRELATION";
}
