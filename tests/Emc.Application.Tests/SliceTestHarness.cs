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

    public IItemHistoryService History
        => new ItemHistoryService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    /// <summary>Signs in as an agent (AR 195-5 2-3b).</summary>
    public void SignInAsAgent() => CurrentUser.SignIn(AgentUserId, "SA SMITH, JOHN A.", EmcRoles.Agent);

    /// <summary>Signs in as the appointed primary evidence custodian (AR 195-5 1-4g(1), 1-4h).</summary>
    public void SignInAsCustodian()
        => CurrentUser.SignIn(CustodianUserId, "SA BAKER, ALICE C.", EmcRoles.PrimaryEvidenceCustodian);

    /// <summary>
    /// Signs in as a user holding the custodian ROLE but with no written appointment - the case
    /// AR 195-5 1-4g(1) makes decisive (IAM-005).
    /// </summary>
    public void SignInAsUnappointedCustodian()
        => CurrentUser.SignIn(
            AlternateCustodianUserId, "SA CHEN, DAVID L.", EmcRoles.AlternateEvidenceCustodian);

    /// <summary>Signs in as an application administrator (IAM-009).</summary>
    public void SignInAsAdministrator()
        => CurrentUser.SignIn(
            AdministratorUserId, "MR. DOE, ROBERT", EmcRoles.ApplicationAdministrator);

    public void SignInAsCommander()
        => CurrentUser.SignIn(CommanderUserId, "MAJ EVANS, SARAH", EmcRoles.CommanderOrSac);

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

        GrantRole(agent.Id, EmcRoles.Agent);
        GrantRole(custodian.Id, EmcRoles.PrimaryEvidenceCustodian);
        GrantRole(alternate.Id, EmcRoles.AlternateEvidenceCustodian);
        GrantRole(administrator.Id, EmcRoles.ApplicationAdministrator);
        GrantRole(commander.Id, EmcRoles.CommanderOrSac);

        // AR 195-5 1-4g(1) - the primary custodian is appointed IN WRITING. Only the appointed
        // custodian holds evidence-room authority; the alternate above is deliberately left
        // unappointed so the tests can prove that the role alone is not enough (IAM-005).
        Db.CustodianAppointments.Add(new CustodianAppointment(
            evidenceRoomId: room.Id,
            userId: custodian.Id,
            appointmentType: CustodianAppointmentType.Primary,
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

    private void GrantRole(int userId, string roleName)
    {
        var role = Db.Roles.Single(r => r.Name == roleName);
        Db.UserRoles.Add(new UserRole(userId, role.Id, userId, Clock.UtcNow));
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
    private readonly List<string> _roles = [];

    public bool IsAuthenticated { get; private set; }
    public int UserId { get; private set; }
    public string DisplayName { get; private set; } = "(unauthenticated)";
    public IReadOnlyCollection<string> Roles => _roles;

    public bool IsInRole(string role) => _roles.Contains(role);

    public void SignIn(int userId, string displayName, params string[] roles)
    {
        IsAuthenticated = true;
        UserId = userId;
        DisplayName = displayName;
        _roles.Clear();
        _roles.AddRange(roles);
    }

    public void SignOut()
    {
        IsAuthenticated = false;
        UserId = 0;
        DisplayName = "(unauthenticated)";
        _roles.Clear();
    }
}

public sealed class TestRequestContext : IRequestContext
{
    public string? SourceAddress => "10.0.0.5";
    public string? CorrelationId => "TEST-CORRELATION";
}
