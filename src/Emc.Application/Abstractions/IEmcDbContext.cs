using Emc.Domain.Cases;
using Emc.Domain.Configuration;
using Emc.Domain.Events;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Abstractions;

/// <summary>
/// The persistence surface application services use. An interface so that use cases can be
/// tested without a database engine, and so the dependency direction stays one-way
/// (Application does not depend on Infrastructure).
/// </summary>
public interface IEmcDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<RoleAssignment> RoleAssignments { get; }
    DbSet<CustodianAppointment> CustodianAppointments { get; }
    DbSet<CustodianDutyAssumption> CustodianDutyAssumptions { get; }
    DbSet<EvidenceRoom> EvidenceRooms { get; }
    DbSet<StorageLocation> StorageLocations { get; }
    DbSet<Case> Cases { get; }
    DbSet<EvidenceVoucher> EvidenceVouchers { get; }
    DbSet<OfficialDocumentNumberAssignment> DocumentNumberAssignments { get; }
    DbSet<EvidenceItem> EvidenceItems { get; }
    DbSet<ItemEvent> ItemEvents { get; }
    DbSet<CustodyParty> CustodyParties { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<SystemConfiguration> SystemConfigurations { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
