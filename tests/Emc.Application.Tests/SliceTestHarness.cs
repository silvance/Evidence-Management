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
public class SliceTestHarness : IDisposable
{
    private readonly SqliteConnection? _connection;

    public SliceTestHarness()
        : this(SqliteOptions(out var connection), useMigrations: false)
    {
        _connection = connection;
    }

    /// <summary>
    /// A harness over any provider. The SQL Server release-validation lane uses this with
    /// <paramref name="useMigrations"/> true, so the schema comes from the committed migrations -
    /// triggers included - rather than from EnsureCreated, which SQLite uses and which builds no
    /// triggers at all.
    /// </summary>
    protected SliceTestHarness(DbContextOptions<EmcDbContext> options, bool useMigrations)
    {
        _options = options;
        Db = new EmcDbContext(_options);

        if (useMigrations)
        {
            Db.Database.Migrate();
        }
        else
        {
            Db.Database.EnsureCreated();
        }

        Clock = new TestClock(new DateTimeOffset(2026, 9, 3, 13, 15, 0, TimeSpan.Zero));
        CurrentUser = new TestCurrentUser();

        Seed();
    }

    private static DbContextOptions<EmcDbContext> SqliteOptions(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        // SQLite does not enforce foreign keys unless asked.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        return new DbContextOptionsBuilder<EmcDbContext>().UseSqlite(connection).Options;
    }

    private readonly DbContextOptions<EmcDbContext> _options;

    public EmcDbContext Db { get; }

    /// <summary>
    /// A second context on the same in-memory database - a second "request". Lets a test hold a
    /// stale tracked row in one context while another commits, which is how a concurrency
    /// conflict is produced without a test seam in production code.
    /// </summary>
    public EmcDbContext CreateSecondContext() => new(_options);

    public TestClock Clock { get; }
    public TestCurrentUser CurrentUser { get; }

    public int EvidenceRoomId { get; private set; }
    public int ShelfBBin14Id { get; private set; }
    public int ShelfBBin19Id { get; private set; }
    public int HighValueSafeId { get; private set; }

    /// <summary>
    /// A second evidence room with a location of its own, so that cross-room checks assert
    /// something real rather than a missing row (invariant I-08, LOC-004).
    /// </summary>
    public int OtherEvidenceRoomId { get; private set; }

    public int OtherRoomLocationId { get; private set; }
    public int AgentUserId { get; private set; }
    public int SecondAgentUserId { get; private set; }
    public string AgentPrintedNameAndGrade { get; private set; } = string.Empty;
    public int CustodianUserId { get; private set; }
    public int AlternateCustodianUserId { get; private set; }
    public int AdministratorUserId { get; private set; }
    public int CommanderUserId { get; private set; }

    /// <summary>The commander's printed name and grade, as the seeded user record has them.</summary>
    public string CommanderPrintedNameAndGrade { get; private set; } = string.Empty;

    public IEvidenceAuthorizationService Authorization
        => new EvidenceAuthorizationService(Db, CurrentUser, Clock);

    public IAuditRecorder Audit => new AuditRecorder(Db, CurrentUser, Clock, new TestRequestContext());

    public IItemEventRecorder EventRecorder => new ItemEventRecorder(Db);

    public ICaseService Cases => new CaseService(Db, Authorization, CurrentUser, Audit, Clock);

    public IVoucherService Vouchers
        => new VoucherService(
            Db, Authorization, CurrentUser, Audit, EventRecorder, Clock,
            new TemporaryIdentifierAllocator(Db));

    public IEvidenceIntakeService Intake
        => new EvidenceIntakeService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    public Reads.IEvidenceReadService Reads
        => new Reads.EvidenceReadService(Db, Authorization, CurrentUser);

    public IItemHistoryService History
        => new ItemHistoryService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    public Filing.IPhysicalDocumentService PhysicalDocuments
        => new Filing.PhysicalDocumentService(Db, Authorization, CurrentUser, Audit, Clock);

    public Suspense.ITemporaryReleaseService Releases
        => new Suspense.TemporaryReleaseService(Db, Authorization, CurrentUser, Audit, EventRecorder, Clock);

    /// <summary>Signs in as an agent in this harness's evidence room (AR 195-5 2-3b).</summary>
    public void SignInAsAgent()
        => CurrentUser.SignIn(AgentUserId, "SA SMITH, JOHN A.", EvidenceRoomId, EmcRoles.Agent);

    public void SignInAsSecondAgent()
        => CurrentUser.SignIn(SecondAgentUserId, "SA PATEL, ANIKA R.", EvidenceRoomId, EmcRoles.Agent);

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
        AgentPrintedNameAndGrade = agent.PrintedNameAndGrade;
        CustodianUserId = custodian.Id;
        AlternateCustodianUserId = alternate.Id;
        AdministratorUserId = administrator.Id;
        CommanderUserId = commander.Id;
        CommanderPrintedNameAndGrade = commander.PrintedNameAndGrade;

        // A second agent in the same room, for "only the SUBMITTING agent" (AR 195-5 2-3g).
        var secondAgent = NewUser("PATEL, ANIKA R.", "SA");
        Db.Users.Add(secondAgent);
        Db.SaveChanges();
        GrantRoleInRoom(secondAgent.Id, EmcRoles.Agent, room.Id);
        Db.SaveChanges();
        SecondAgentUserId = secondAgent.Id;

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
        var bin19 = new StorageLocation(room.Id, "Bin 19", StorageLocationKind.Bin, shelfB);
        var safe = new StorageLocation(
            room.Id, "High-Value Safe / Drawer 2", StorageLocationKind.HighValueContainer);

        Db.StorageLocations.AddRange(bin14, bin19, safe);
        Db.SaveChanges();

        ShelfBBin14Id = bin14.Id;
        ShelfBBin19Id = bin19.Id;
        HighValueSafeId = safe.Id;

        // A different evidence room, with its own storage. Nothing in the seeded user set holds
        // any role in it.
        var otherRoom = new EvidenceRoom(
            "310th MI Bn Evidence Room", "310th MI Bn", "America/New_York");

        Db.EvidenceRooms.Add(otherRoom);
        Db.SaveChanges();

        var otherBin = new StorageLocation(otherRoom.Id, "Bin 1", StorageLocationKind.Bin);
        Db.StorageLocations.Add(otherBin);
        Db.SaveChanges();

        OtherEvidenceRoomId = otherRoom.Id;
        OtherRoomLocationId = otherBin.Id;
    }

    private static User NewUser(string name, string grade)
    {
        var user = new User($"S-1-5-21-{Guid.NewGuid():N}", $"{name.Replace(", ", ".", StringComparison.Ordinal)}@army.mil", name);
        user.UpdateProfile(name, grade, "902d MI Group");
        return user;
    }

    /// <summary>
    /// Records a numbering policy for the main room, ending any policy open at that instant
    /// (VCH-023). Returns the new policy.
    /// </summary>
    public EvidenceRoomNumberingPolicy UseNumberingPolicy(
        DocumentNumberLayout layout,
        int sequenceWidth,
        NumberingPolicyBasis basis,
        string? authorityReference,
        DateTimeOffset? effectiveFrom = null)
    {
        var from = effectiveFrom ?? Clock.UtcNow.AddYears(-1);

        foreach (var open in Db.EvidenceRoomNumberingPolicies
                     .Where(p => p.EvidenceRoomId == EvidenceRoomId && p.EffectiveTo == null)
                     .ToList())
        {
            open.EndAt(from);
        }

        var policy = new EvidenceRoomNumberingPolicy(
            EvidenceRoomId, from, layout, sequenceWidth, 2, "-", basis, authorityReference, null);

        Db.EvidenceRoomNumberingPolicies.Add(policy);
        Db.SaveChanges();
        return policy;
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
        CustodianAppointment alternateAppointment,
        DateTimeOffset? assumedAt = null,
        DateTimeOffset? absenceStart = null)
    {
        var primary = Db.CustodianAppointments.First(
            a => a.EvidenceRoomId == EvidenceRoomId
                 && a.AppointmentType == CustodianAppointmentType.Primary);

        var at = assumedAt ?? Clock.UtcNow;
        var start = absenceStart ?? at;

        var assumption = new CustodianDutyAssumption(
            evidenceRoomId: EvidenceRoomId,
            primaryAppointmentId: primary.Id,
            alternateAppointmentId: alternateAppointment.Id,
            alternateUserId: alternateAppointment.UserId,
            primaryAbsenceStart: start,
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

    /// <summary>
    /// AR 195-5 3-2d - appoints a user primary evidence custodian on orders, ending the existing
    /// primary appointment. Used to test the transition after a long absence.
    /// </summary>
    public CustodianAppointment AppointAsPrimary(int userId, DateTimeOffset? from = null)
    {
        var at = from ?? Clock.UtcNow;

        var existing = Db.CustodianAppointments.FirstOrDefault(
            a => a.EvidenceRoomId == EvidenceRoomId
                 && a.AppointmentType == CustodianAppointmentType.Primary
                 && a.EffectiveTo == null);

        existing?.End(at);

        var appointment = new CustodianAppointment(
            evidenceRoomId: EvidenceRoomId,
            userId: userId,
            appointmentType: CustodianAppointmentType.Primary,
            personnelCategory: PersonnelCategory.MilitaryCi,
            effectiveFrom: at,
            appointmentOrderReference: "ORDERS 2026-201, 902d MI Group",
            appointingAuthority: "Commander, 902d MI Group",
            eligibilityAttested: true,
            recordedByUserId: CommanderUserId,
            recordedAtUtc: at);

        Db.CustodianAppointments.Add(appointment);
        Db.SaveChanges();
        return appointment;
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

    public virtual void Dispose()
    {
        Db.Dispose();
        _connection?.Dispose();
        GC.SuppressFinalize(this);
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
