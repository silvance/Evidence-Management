using Emc.Domain.Cases;
using Emc.Domain.Documents;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class SourceDocumentConfiguration : IEntityTypeConfiguration<SourceDocument>
{
    public void Configure(EntityTypeBuilder<SourceDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SourceDocuments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.DocumentType).HasConversion<int>().IsRequired();
        builder.Property(d => d.Provenance).HasConversion<int>().IsRequired();
        builder.Property(d => d.ImportStatus).HasConversion<int>().IsRequired();
        builder.Property(d => d.OriginalFilename).HasMaxLength(260).IsRequired();
        builder.Property(d => d.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(d => d.StorageKey).HasMaxLength(128).IsRequired();
        builder.Property(d => d.ClassificationMarking).HasMaxLength(128).IsRequired();
        builder.Property(d => d.ProvenanceNotes).HasMaxLength(2000);

        builder.HasOne<EvidenceRoom>().WithMany().HasForeignKey(d => d.EvidenceRoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Case>().WithMany().HasForeignKey(d => d.CaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceVoucher>().WithMany().HasForeignKey(d => d.VoucherId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.ReceivedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.StorageKey).IsUnique();
        builder.HasIndex(d => new { d.EvidenceRoomId, d.Sha256 });
        builder.HasIndex(d => d.VoucherId);

        builder.HasMany(d => d.Pages).WithOne(p => p.Document).HasForeignKey(p => p.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SourceDocumentPageConfiguration : IEntityTypeConfiguration<SourceDocumentPage>
{
    public void Configure(EntityTypeBuilder<SourceDocumentPage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SourceDocumentPages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.StorageKey).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(p => p.RendererVersion).HasMaxLength(256).IsRequired();
        builder.HasIndex(p => new { p.SourceDocumentId, p.PageNumber }).IsUnique();
        builder.HasIndex(p => p.StorageKey).IsUnique();
    }
}
