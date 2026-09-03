using Emc.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Users");
        builder.HasKey(u => u.Id);

        // IAM-003: there is deliberately no password, password hash or secret column here.
        // Authentication is Windows Authentication; the AD object SID is the stable key.
        builder.Property(u => u.ActiveDirectorySid).HasMaxLength(184).IsRequired();
        builder.Property(u => u.UserPrincipalName).HasMaxLength(256).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(u => u.RankOrGrade).HasMaxLength(64);
        builder.Property(u => u.OrganizationOrUnit).HasMaxLength(256);
        builder.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasIndex(u => u.ActiveDirectorySid).IsUnique();
        builder.HasIndex(u => u.UserPrincipalName).IsUnique();

        builder.Ignore(u => u.PrintedNameAndGrade);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(512).IsRequired();
        builder.HasIndex(r => r.Name).IsUnique();
    }
}

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RoleAssignments");
        builder.HasKey(ra => ra.Id);

        builder.HasOne(ra => ra.User)
            .WithMany()
            .HasForeignKey(ra => ra.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(ra => ra.Role)
            .WithMany()
            .HasForeignKey(ra => ra.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        // Null EvidenceRoomId means a global grant, which only the administrator role may hold
        // (IAM-016). The nullable FK is what makes room scoping expressible at all.
        builder.HasOne<Domain.Storage.EvidenceRoom>()
            .WithMany()
            .HasForeignKey(ra => ra.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        // One open grant of a role per user per room. Filtered so a revoked grant does not block
        // re-granting the same role later.
        builder.HasIndex(ra => new { ra.UserId, ra.RoleId, ra.EvidenceRoomId })
            .HasDatabaseName("UX_RoleAssignments_OneOpenPerUserRoleRoom")
            .HasFilter("EffectiveTo IS NULL")
            .IsUnique();

        builder.HasIndex(ra => new { ra.UserId, ra.EvidenceRoomId });

        builder.Ignore(ra => ra.IsSelfGrant);
    }
}

public sealed class CustodianAppointmentConfiguration : IEntityTypeConfiguration<CustodianAppointment>
{
    public void Configure(EntityTypeBuilder<CustodianAppointment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CustodianAppointments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.AppointmentOrderReference).HasMaxLength(256).IsRequired();
        builder.Property(a => a.AppointingAuthority).HasMaxLength(256).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(2000);
        builder.Property(a => a.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Domain.Storage.EvidenceRoom>()
            .WithMany()
            .HasForeignKey(a => a.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        // AR 195-5 1-4g(1) requires exactly ONE primary and ONE alternate at a time
        // (invariant I-06). A filtered unique index over currently-open, non-superseded
        // appointments enforces it in the database rather than relying on application checks.
        //
        // Note the limit of this constraint: it prevents two OPEN appointments of the same type
        // for one evidence room, which is the case that actually occurs. It cannot express
        // "no two appointments whose date ranges overlap" in a portable index, so the
        // application also checks for overlap when recording a closed-ended appointment.
        //
        // Unquoted identifiers in the filter so the same expression is valid on SQL Server
        // (production) and SQLite (tests).
        builder.HasIndex(a => new { a.EvidenceRoomId, a.AppointmentType })
            .HasDatabaseName("UX_CustodianAppointments_OneOpenPerType")
            .HasFilter("EffectiveTo IS NULL AND SupersededByAppointmentId IS NULL")
            .IsUnique();

        builder.HasIndex(a => new { a.EvidenceRoomId, a.UserId, a.EffectiveFrom });

        builder.Property(a => a.PersonnelCategory).HasConversion<int>().IsRequired();
        builder.Ignore(a => a.EligibilityRegulatoryBasis);
        builder.Ignore(a => a.EligibilityStatement);
    }
}

public sealed class CustodianDutyAssumptionConfiguration
    : IEntityTypeConfiguration<CustodianDutyAssumption>
{
    public void Configure(EntityTypeBuilder<CustodianDutyAssumption> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CustodianDutyAssumptions");
        builder.HasKey(d => d.Id);

        // AR 195-5 1-7c(1) and 1-7c(2) - the handwritten, signed ledger statements. EMC records
        // that they were made on paper; it does not produce them (AUD-013).
        builder.Property(d => d.AssumptionLedgerAttestation).HasMaxLength(2000).IsRequired();
        builder.Property(d => d.ResumptionLedgerAttestation).HasMaxLength(2000);
        builder.Property(d => d.ReasonForAbsence).HasMaxLength(1000);
        builder.Property(d => d.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne<CustodianAppointment>()
            .WithMany()
            .HasForeignKey(d => d.PrimaryAppointmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CustodianAppointment>()
            .WithMany()
            .HasForeignKey(d => d.AlternateAppointmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Domain.Storage.EvidenceRoom>()
            .WithMany()
            .HasForeignKey(d => d.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        // At most one open assumption per evidence room: only one person acts as the evidence
        // custodian at a time (AR 195-5 1-4g(1), 1-4i).
        builder.HasIndex(d => d.EvidenceRoomId)
            .HasDatabaseName("UX_CustodianDutyAssumptions_OneOpenPerRoom")
            .HasFilter("PrimaryResumedAt IS NULL")
            .IsUnique();

        builder.HasIndex(d => new { d.AlternateUserId, d.EvidenceRoomId });

        builder.Ignore(d => d.RequiresHundredPercentInventoryOnResumption);
    }
}
