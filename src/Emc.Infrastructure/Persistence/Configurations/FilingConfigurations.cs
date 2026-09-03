using Emc.Domain.Cases;
using Emc.Domain.Filing;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class PhysicalFileContainerConfiguration : IEntityTypeConfiguration<PhysicalFileContainer>
{
    public void Configure(EntityTypeBuilder<PhysicalFileContainer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PhysicalFileContainers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Kind).HasConversion<int>().IsRequired();
        builder.Property(c => c.Form).HasConversion<int>().IsRequired();
        builder.Property(c => c.Label).HasMaxLength(256).IsRequired();
        builder.Property(c => c.DocumentNumberRangeFrom).HasMaxLength(24);
        builder.Property(c => c.DocumentNumberRangeTo).HasMaxLength(24);
        builder.Property(c => c.Notes).HasMaxLength(2000);
        builder.Property(c => c.ConcurrencyStamp).IsConcurrencyToken();
        builder.Ignore(c => c.DispositionLabel);
        builder.Ignore(c => c.IsSuspense);

        builder.HasOne(c => c.EvidenceRoom).WithMany().HasForeignKey(c => c.EvidenceRoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.EvidenceRoomId, c.Kind, c.Label }).IsUnique();
    }
}

public sealed class PhysicalVoucherDocumentConfiguration : IEntityTypeConfiguration<PhysicalVoucherDocument>
{
    public void Configure(EntityTypeBuilder<PhysicalVoucherDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PhysicalVoucherDocuments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.OriginalStatus).HasConversion<int>().IsRequired();
        builder.Property(d => d.CopyReason).HasConversion<int>().IsRequired();
        builder.Property(d => d.ConcurrencyStamp).IsConcurrencyToken();

        // One paper record per voucher: the DA Form 4137 is filed as a whole.
        builder.HasIndex(d => d.VoucherId).IsUnique();
        builder.HasOne<EvidenceVoucher>().WithMany().HasForeignKey(d => d.VoucherId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRoom>().WithMany().HasForeignKey(d => d.EvidenceRoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PhysicalFileContainer>().WithMany().HasForeignKey(d => d.OriginalContainerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PhysicalFileContainer>().WithMany().HasForeignKey(d => d.SuspenseCopyContainerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PhysicalFileContainer>().WithMany().HasForeignKey(d => d.InactiveContainerId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(d => d.Events).WithOne(e => e.Document).HasForeignKey(e => e.DocumentId).OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(d => d.OriginalHeldHere);
        builder.Ignore(d => d.OriginalIsOut);
        builder.Ignore(d => d.IsInactive);
        builder.Ignore(d => d.DestructionEligibleAtUtc);
    }
}

public sealed class PhysicalVoucherDocumentEventConfiguration : IEntityTypeConfiguration<PhysicalVoucherDocumentEvent>
{
    public void Configure(EntityTypeBuilder<PhysicalVoucherDocumentEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PhysicalVoucherDocumentEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).HasConversion<int>().IsRequired();
        builder.Property(e => e.ResultingOriginalStatus).HasConversion<int>().IsRequired();
        builder.Property(e => e.Narrative).HasMaxLength(2000);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.DocumentId, e.OccurredAtUtc });
    }
}
