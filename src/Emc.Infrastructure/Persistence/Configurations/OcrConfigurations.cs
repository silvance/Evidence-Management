using Emc.Domain.Documents;
using Emc.Domain.Identity;
using Emc.Domain.Ocr;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class OcrJobConfiguration : IEntityTypeConfiguration<OcrJob>
{
    public void Configure(EntityTypeBuilder<OcrJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("OcrJobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Status).HasConversion<int>().IsRequired();
        builder.Property(j => j.LastFailureCategory).HasConversion<int>().IsRequired();
        builder.Property(j => j.LeasedByWorkerId).HasMaxLength(128);
        builder.Property(j => j.ConcurrencyStamp).IsConcurrencyToken();
        builder.HasOne<SourceDocument>().WithMany().HasForeignKey(j => j.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentRenderRun>().WithMany().HasForeignKey(j => j.RenderRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceRoom>().WithMany().HasForeignKey(j => j.EvidenceRoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(j => j.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(j => new { j.Status, j.RequestedAtUtc });
        builder.HasIndex(j => j.SourceDocumentId);
    }
}

public sealed class OcrRunConfiguration : IEntityTypeConfiguration<OcrRun>
{
    public void Configure(EntityTypeBuilder<OcrRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("OcrRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.WorkerId).HasMaxLength(128).IsRequired();
        builder.Property(r => r.EngineName).HasMaxLength(64).IsRequired();
        builder.Property(r => r.EngineVersion).HasMaxLength(128).IsRequired();
        builder.Property(r => r.ModelIdentifiers).HasMaxLength(1024).IsRequired();
        builder.Property(r => r.PreprocessingVersion).HasMaxLength(256).IsRequired();
        builder.Property(r => r.TemplateId).HasMaxLength(64);
        builder.Property(r => r.Outcome).HasConversion<int>().IsRequired();
        builder.Property(r => r.FailureCategory).HasConversion<int>().IsRequired();
        builder.HasOne<OcrJob>().WithMany().HasForeignKey(r => r.OcrJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceDocument>().WithMany().HasForeignKey(r => r.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DocumentRenderRun>().WithMany().HasForeignKey(r => r.RenderRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.SourceDocumentId, r.CompletedAtUtc });
        builder.HasMany(r => r.Fields).WithOne(f => f.Run).HasForeignKey(f => f.OcrRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Pages).WithOne(p => p.Run).HasForeignKey(p => p.OcrRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OcrRunPageConfiguration : IEntityTypeConfiguration<OcrRunPage>
{
    public void Configure(EntityTypeBuilder<OcrRunPage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("OcrRunPages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.StorageKey).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasIndex(p => new { p.OcrRunId, p.PageNumber }).IsUnique();
        builder.HasIndex(p => p.StorageKey).IsUnique();
    }
}

public sealed class ExtractedFieldConfiguration : IEntityTypeConfiguration<ExtractedField>
{
    public void Configure(EntityTypeBuilder<ExtractedField> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ExtractedFields");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.FieldKey).HasMaxLength(128).IsRequired();
        builder.Property(f => f.RawText).HasMaxLength(ExtractedField.MaxRawTextLength).IsRequired();
        builder.Property(f => f.NormalizedCandidate).HasMaxLength(ExtractedField.MaxRawTextLength);
        builder.Property(f => f.Confidence).HasPrecision(5, 2);
        builder.Property(f => f.Band).HasConversion<int>().IsRequired();
        builder.HasIndex(f => new { f.OcrRunId, f.PageNumber });
        builder.HasMany(f => f.Verifications).WithOne(v => v.Field).HasForeignKey(v => v.ExtractedFieldId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FieldVerificationConfiguration : IEntityTypeConfiguration<FieldVerification>
{
    public void Configure(EntityTypeBuilder<FieldVerification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("FieldVerifications");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Decision).HasConversion<int>().IsRequired();
        builder.Property(v => v.EnteredValue).HasMaxLength(FieldVerification.MaxValueLength);
        builder.Property(v => v.Note).HasMaxLength(2000);
        builder.HasOne<User>().WithMany().HasForeignKey(v => v.VerifiedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(v => new { v.ExtractedFieldId, v.VerifiedAtUtc });
    }
}
