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

        builder.HasMany(u => u.Roles)
            .WithOne(r => r.User)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

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

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserRoles");
        builder.HasKey(ur => ur.Id);

        builder.HasOne(ur => ur.Role)
            .WithMany()
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ur => new { ur.UserId, ur.RoleId }).IsUnique();
        builder.Ignore(ur => ur.IsSelfGrant);
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
    }
}
