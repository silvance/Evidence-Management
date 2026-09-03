using Emc.Domain.Cases;
using Emc.Domain.Documents;
using Emc.Domain.Identity;
using Emc.Domain.Ocr;
using Emc.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class ReconciliationFindingConfiguration : IEntityTypeConfiguration<ReconciliationFinding>
{
    public void Configure(EntityTypeBuilder<ReconciliationFinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ReconciliationFindings");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.FieldKey).HasMaxLength(128).IsRequired();
        builder.Property(f => f.CompanionValue).HasMaxLength(ReconciliationFinding.MaxValueLength);
        builder.Property(f => f.DocumentValue).HasMaxLength(ReconciliationFinding.MaxValueLength);
        builder.Property(f => f.Narrative).HasMaxLength(ReconciliationFinding.MaxValueLength);
        builder.Property(f => f.Kind).HasConversion<int>().IsRequired();
        builder.Property(f => f.Decision).HasConversion<int>().IsRequired();
        builder.HasOne<OcrRun>().WithMany().HasForeignKey(f => f.OcrRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SourceDocument>().WithMany().HasForeignKey(f => f.SourceDocumentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceVoucher>().WithMany().HasForeignKey(f => f.VoucherId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EvidenceItem>().WithMany().HasForeignKey(f => f.EvidenceItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(f => f.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(f => new { f.SourceDocumentId, f.FieldKey });
        builder.HasIndex(f => f.VoucherId);
    }
}
