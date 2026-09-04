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
    }
}

/// <summary>Work record (DOC-014): mutable under the concurrency stamp, like an OCR job.</summary>
public sealed class DocumentRenderJobConfiguration : IEntityTypeConfiguration<DocumentRenderJob>
{
    public void Configure(EntityTypeBuilder<DocumentRenderJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("DocumentRenderJobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Status).HasConversion<int>().IsRequired();
        builder.Property(j => j.LastFailureCategory).HasConversion<int>().IsRequired();
        builder.Property(j => j.LeasedByWorkerId).HasMaxLength(128);
        builder.Property(j => j.ConcurrencyStamp).IsConcurrencyToken();
        builder.HasOne(j => j.Document).WithMany().HasForeignKey(j => j.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRoom>().WithMany().HasForeignKey(j => j.EvidenceRoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(j => j.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(j => new { j.Status, j.RequestedAtUtc });
        builder.HasIndex(j => j.SourceDocumentId);
        builder.Ignore(j => j.IsOpen);
    }
}

/// <summary>Immutable attempt record (DOC-015): append-only at the SaveChanges guard and the SQL trigger.</summary>
public sealed class DocumentRenderRunConfiguration : IEntityTypeConfiguration<DocumentRenderRun>
{
    public void Configure(EntityTypeBuilder<DocumentRenderRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("DocumentRenderRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.WorkerId).HasMaxLength(128).IsRequired();
        builder.Property(r => r.RendererVersion).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Outcome).HasConversion<int>().IsRequired();
        builder.Property(r => r.FailureCategory).HasConversion<int>().IsRequired();
        builder.HasOne<DocumentRenderJob>().WithMany().HasForeignKey(r => r.RenderJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceDocument>().WithMany().HasForeignKey(r => r.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.SourceDocumentId, r.Outcome, r.CompletedAtUtc });
        builder.HasMany(r => r.Pages).WithOne(p => p.Run).HasForeignKey(p => p.RenderRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DocumentRenderPageConfiguration : IEntityTypeConfiguration<DocumentRenderPage>
{
    public void Configure(EntityTypeBuilder<DocumentRenderPage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("DocumentRenderPages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.StorageKey).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasIndex(p => new { p.RenderRunId, p.PageNumber }).IsUnique();
        builder.HasIndex(p => p.StorageKey).IsUnique();
    }
}
