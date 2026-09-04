using Emc.Application.Abstractions;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Configuration;
using Emc.Domain.Events;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Emc.Infrastructure.Persistence;

public sealed class EmcDbContext : DbContext, IEmcDbContext
{
    public EmcDbContext(DbContextOptions<EmcDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<CustodianAppointment> CustodianAppointments => Set<CustodianAppointment>();
    public DbSet<CustodianDutyAssumption> CustodianDutyAssumptions => Set<CustodianDutyAssumption>();
    public DbSet<PrimaryCustodianTransition> PrimaryCustodianTransitions => Set<PrimaryCustodianTransition>();
    public DbSet<EvidenceRoom> EvidenceRooms => Set<EvidenceRoom>();
    public DbSet<EvidenceRoomNumberingPolicy> EvidenceRoomNumberingPolicies => Set<EvidenceRoomNumberingPolicy>();
    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();
    public DbSet<Case> Cases => Set<Case>();
    public DbSet<EvidenceVoucher> EvidenceVouchers => Set<EvidenceVoucher>();
    public DbSet<TemporaryIdentifierCounter> TemporaryIdentifierCounters => Set<TemporaryIdentifierCounter>();
    public DbSet<VoucherReviewAction> VoucherReviewActions => Set<VoucherReviewAction>();
    public DbSet<VoucherFormRevision> VoucherFormRevisions => Set<VoucherFormRevision>();
    public DbSet<Emc.Domain.Filing.PhysicalFileContainer> PhysicalFileContainers => Set<Emc.Domain.Filing.PhysicalFileContainer>();
    public DbSet<Emc.Domain.Filing.PhysicalVoucherDocument> PhysicalVoucherDocuments => Set<Emc.Domain.Filing.PhysicalVoucherDocument>();
    public DbSet<Emc.Domain.Documents.SourceDocument> SourceDocuments => Set<Emc.Domain.Documents.SourceDocument>();
    public DbSet<Emc.Domain.Documents.DocumentRenderJob> DocumentRenderJobs => Set<Emc.Domain.Documents.DocumentRenderJob>();
    public DbSet<Emc.Domain.Documents.DocumentRenderRun> DocumentRenderRuns => Set<Emc.Domain.Documents.DocumentRenderRun>();
    public DbSet<Emc.Domain.Documents.DocumentRenderPage> DocumentRenderPages => Set<Emc.Domain.Documents.DocumentRenderPage>();
    public DbSet<Emc.Domain.Ocr.OcrJob> OcrJobs => Set<Emc.Domain.Ocr.OcrJob>();
    public DbSet<Emc.Domain.Ocr.OcrRun> OcrRuns => Set<Emc.Domain.Ocr.OcrRun>();
    public DbSet<Emc.Domain.Reconciliation.ReconciliationFinding> ReconciliationFindings => Set<Emc.Domain.Reconciliation.ReconciliationFinding>();
    public DbSet<Emc.Domain.Suspense.TemporaryRelease> TemporaryReleases => Set<Emc.Domain.Suspense.TemporaryRelease>();
    public DbSet<Emc.Domain.Suspense.TemporaryReleaseItem> TemporaryReleaseItems => Set<Emc.Domain.Suspense.TemporaryReleaseItem>();
    public DbSet<Emc.Domain.Suspense.SuspenseContact> SuspenseContacts => Set<Emc.Domain.Suspense.SuspenseContact>();
    public DbSet<OfficialDocumentNumberAssignment> DocumentNumberAssignments
        => Set<OfficialDocumentNumberAssignment>();
    public DbSet<EvidenceItem> EvidenceItems => Set<EvidenceItem>();
    public DbSet<ItemEvent> ItemEvents => Set<ItemEvent>();
    public DbSet<CustodyParty> CustodyParties => Set<CustodyParty>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();

    /// <summary>
    /// SQLite cannot compare or order <see cref="DateTimeOffset"/> values, so a test run over
    /// SQLite would fail on queries that SQL Server handles natively (for example, finding the
    /// custodian appointment in force right now). Storing them as UTC ticks keeps those queries
    /// translatable and correctly ordered under the test provider.
    ///
    /// This applies ONLY to SQLite. Under SQL Server - the production provider - DateTimeOffset
    /// is stored as datetimeoffset and compared natively, unchanged by this.
    ///
    /// The offset itself is never lost: every event stores OccurredAtOffset separately, because
    /// the DA Form 4137 and the evidence ledger record LOCAL time and EMC must be able to render
    /// what the paper says (AUD-011).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Provider detected by name rather than with Database.IsSqlite(), so the production
        // assembly carries no SQLite package reference at all - SQLite is a test-only concern.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true)
        {
            configurationBuilder
                .Properties<DateTimeOffset>()
                .HaveConversion<DateTimeOffsetToBinaryConverter>();

            configurationBuilder
                .Properties<DateTimeOffset?>()
                .HaveConversion<DateTimeOffsetToBinaryConverter>();
        }

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EmcDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges()
    {
        EnforceInvariants();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceInvariants();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Layer 2 of the three-layer append-only enforcement (docs/architecture.md §4.2).
    ///
    /// Layer 1 is the domain: append-only types have no public setters on accountability fields.
    /// Layer 3 is the database: INSTEAD OF UPDATE, DELETE triggers, which also catch changes made
    /// outside the application — including by a DBA in SSMS.
    ///
    /// This layer catches mistakes made through EF regardless of which service made them, which
    /// is the case the other two miss: a service that loads an event, sets a property and saves.
    /// </summary>
    private void EnforceInvariants()
    {
        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnly)
            {
                GuardAppendOnly(entry);
            }

            // Optimistic concurrency: a fresh stamp on every update means a concurrent writer
            // whose stamp is stale fails rather than silently overwriting (SEC-007).
            if (entry is { Entity: IConcurrencyStamped stamped, State: EntityState.Modified })
            {
                stamped.ConcurrencyStamp = Guid.NewGuid();
            }

            // AUD-008 — an accountability event must never reach the database unhashed, or the
            // chain would have a hole that verification could not distinguish from tampering.
            if (entry is { Entity: ItemEvent itemEvent, State: EntityState.Added }
                && string.IsNullOrEmpty(itemEvent.EventHash))
            {
                throw new AppendOnlyViolationException(
                    $"Item event of kind {itemEvent.Kind} was not sealed into the item's hash "
                    + "chain. Append events through IItemEventRecorder.");
            }
        }
    }

    /// <summary>
    /// INSERT ONLY. No update is permitted, not even a narrow one.
    ///
    /// An earlier design allowed exactly one mutation - a forward "superseded by" pointer - which
    /// forced the database trigger to prove every OTHER column was unchanged. In a
    /// table-per-hierarchy table that is easy to get wrong, and the trigger in fact compared only
    /// the columns common to all event types: subtype columns such as StorageLocationPath and
    /// PurposeOfChangeOfCustody could be rewritten alongside a legitimate supersession.
    ///
    /// Corrections now use backward references, so nothing ever updates a recorded event and this
    /// guard needs no exceptions at all.
    /// </summary>
    private static void GuardAppendOnly(EntityEntry entry)
    {
        switch (entry.State)
        {
            case EntityState.Deleted:
                throw new AppendOnlyViolationException(
                    $"{entry.Entity.GetType().Name} records are append-only and cannot be deleted. "
                    + "AR 195-5 para 2-5b(5) requires an erroneous entry to remain readable: it is "
                    + "voided with a single line drawn through it and initialed, never erased. "
                    + "Record a correction instead.");

            case EntityState.Modified:
            {
                var modified = entry.Properties
                    .Where(p => p.IsModified)
                    .Select(p => p.Metadata.Name)
                    .ToList();

                throw new AppendOnlyViolationException(
                    $"{entry.Entity.GetType().Name} records are append-only and cannot be modified. "
                    + $"Attempted to modify: {string.Join(", ", modified)}. AR 195-5 para 2-5b(5) "
                    + "prohibits erasing an entry; para 1-7c(3) requires the error and the "
                    + "corrective action to be recorded. Record a correction instead.");
            }

            case EntityState.Added:
            case EntityState.Unchanged:
            case EntityState.Detached:
            default:
                break;
        }
    }
}
