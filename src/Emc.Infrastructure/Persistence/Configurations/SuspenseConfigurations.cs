using Emc.Domain.Cases;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Emc.Domain.Suspense;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class TemporaryReleaseConfiguration : IEntityTypeConfiguration<TemporaryRelease>
{
    public void Configure(EntityTypeBuilder<TemporaryRelease> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TemporaryReleases");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Category).HasConversion<int>().IsRequired();
        builder.Property(r => r.Status).HasConversion<int>().IsRequired();
        builder.Property(r => r.Purpose).HasMaxLength(1000).IsRequired();
        builder.Property(r => r.Destination).HasMaxLength(512);
        builder.Property(r => r.Notes).HasMaxLength(2000);
        builder.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne<EvidenceVoucher>().WithMany().HasForeignKey(r => r.VoucherId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRoom>().WithMany().HasForeignKey(r => r.EvidenceRoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.ReleasedBy).WithMany().HasForeignKey(r => r.ReleasedByPartyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.ReceivedBy).WithMany().HasForeignKey(r => r.ReceivedByPartyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PhysicalFileContainer>().WithMany().HasForeignKey(r => r.SuspenseFolderContainerId).OnDelete(DeleteBehavior.Restrict);

        // The 2-7b attestations: records that paper acts occurred, owned by the release.
        builder.OwnsOne(r => r.Attestations, a =>
        {
            a.Property(x => x.PhysicalInventoryPerformedAttested).HasColumnName("PhysicalInventoryPerformedAttested");
            a.Property(x => x.Original4137ReceivedBySignedAttested).HasColumnName("Original4137ReceivedBySignedAttested");
            a.Property(x => x.FirstCopyReceivedBySignedAttested).HasColumnName("FirstCopyReceivedBySignedAttested");
            a.Property(x => x.IdentificationPresentedAttested).HasColumnName("IdentificationPresentedAttested");
            a.Property(x => x.ObligationsInformedAttested).HasColumnName("ObligationsInformedAttested");
        });
        builder.Navigation(r => r.Attestations).IsRequired();

        builder.HasMany(r => r.Items).WithOne(i => i.Release).HasForeignKey(i => i.TemporaryReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Events).WithOne(e => e.Release).HasForeignKey(e => e.TemporaryReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Contacts).WithOne(c => c.Release).HasForeignKey(c => c.TemporaryReleaseId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.EvidenceRoomId, r.Status, r.ReleasedAtUtc });
        builder.HasIndex(r => r.VoucherId);
        builder.Ignore(r => r.IsOpen);
        builder.Ignore(r => r.ItemsOut);
        builder.Ignore(r => r.LastContactAtUtc);
    }
}

public sealed class TemporaryReleaseItemConfiguration : IEntityTypeConfiguration<TemporaryReleaseItem>
{
    public void Configure(EntityTypeBuilder<TemporaryReleaseItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TemporaryReleaseItems");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Status).HasConversion<int>().IsRequired();
        builder.HasOne<EvidenceItem>().WithMany().HasForeignKey(i => i.EvidenceItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.ReleaseCustodyEvent).WithMany().HasForeignKey(i => i.ReleaseCustodyEventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.ReturnCustodyEvent).WithMany().HasForeignKey(i => i.ReturnCustodyEventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(i => new { i.TemporaryReleaseId, i.EvidenceItemId }).IsUnique();

        // SUSP-001 at the database: an item is OUT on at most one release at a time (Status Out = 1).
        builder.HasIndex(i => i.EvidenceItemId)
            .HasDatabaseName("UX_TemporaryReleaseItems_OneOpenPerItem")
            .IsUnique()
            .HasFilter("Status = 1");
    }
}

public sealed class TemporaryReleaseEventConfiguration : IEntityTypeConfiguration<TemporaryReleaseEvent>
{
    public void Configure(EntityTypeBuilder<TemporaryReleaseEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TemporaryReleaseEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).HasConversion<int>().IsRequired();
        builder.Property(e => e.Narrative).HasMaxLength(2000);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceItem>().WithMany().HasForeignKey(e => e.EvidenceItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.TemporaryReleaseId, e.OccurredAtUtc });
    }
}

public sealed class SuspenseContactConfiguration : IEntityTypeConfiguration<SuspenseContact>
{
    public void Configure(EntityTypeBuilder<SuspenseContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("SuspenseContacts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Method).HasConversion<int>().IsRequired();
        builder.Property(c => c.Outcome).HasConversion<int>().IsRequired();
        builder.Property(c => c.ContactedPerson).HasMaxLength(256).IsRequired();
        builder.Property(c => c.Narrative).HasMaxLength(2000);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TemporaryReleaseId, c.ContactedAtUtc });
    }
}
