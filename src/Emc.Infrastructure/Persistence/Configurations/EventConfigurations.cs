using Emc.Domain.Configuration;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table-per-hierarchy mapping for the accountability event store.
///
/// Subtypes stay separate C# classes with distinct required fields; they share one table so that
/// item history is a single indexed query and the append-only guard, correction mechanism and
/// hash chain are each implemented once (docs/architecture.md §4.1).
/// </summary>
public sealed class ItemEventConfiguration : IEntityTypeConfiguration<ItemEvent>
{
    public void Configure(EntityTypeBuilder<ItemEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ItemEvents");
        builder.HasKey(e => e.Id);

        builder.HasDiscriminator<string>("EventKind")
            .HasValue<CustodyEvent>("Custody")
            .HasValue<LocationEvent>("Location")
            .HasValue<SealEvent>("Seal")
            .HasValue<ExaminationEvent>("Examination")
            .HasValue<StatusEvent>("Status")
            .HasValue<DocumentNumberEvent>("DocumentNumber")
            .HasValue<CorrectionEvent>("Correction");

        builder.Property(e => e.Notes).HasMaxLength(4000);
        builder.Property(e => e.PreviousEventHash).HasMaxLength(64);
        builder.Property(e => e.EventHash).HasMaxLength(64).IsRequired();

        // Invariant I-07 - sequence numbers are unique per item, which is also what makes a
        // removed row detectable as a gap during chain verification (AUD-008).
        builder.HasIndex(e => new { e.EvidenceItemId, e.SequenceNumber })
            .HasDatabaseName("UX_ItemEvents_ItemSequence")
            .IsUnique();

        // The item history query: every event for one item, in chronological order.
        builder.HasIndex(e => new { e.EvidenceItemId, e.OccurredAtUtc, e.SequenceNumber })
            .HasDatabaseName("IX_ItemEvents_ItemChronology");

        builder.Ignore(e => e.Kind);

        // Correctable fields are computed from the event's own state, not stored.
        builder.Ignore(e => e.CorrectableFields);

        // Derived from the event's own columns; there is nothing separate to persist.
        builder.Ignore(e => e.ReferenceFields);
    }
}

public sealed class CustodyEventConfiguration : IEntityTypeConfiguration<CustodyEvent>
{
    public void Configure(EntityTypeBuilder<CustodyEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(e => e.PurposeOfChangeOfCustody).HasMaxLength(1000);
        builder.Property(e => e.Destination).HasMaxLength(512);
        builder.Property(e => e.Agency).HasMaxLength(256);

        builder.HasOne(e => e.ReleasedBy)
            .WithMany()
            .HasForeignKey(e => e.ReleasedByPartyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.ReceivedBy)
            .WithMany()
            .HasForeignKey(e => e.ReceivedByPartyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(e => e.PurposeForForm);
    }
}

public sealed class LocationEventConfiguration : IEntityTypeConfiguration<LocationEvent>
{
    public void Configure(EntityTypeBuilder<LocationEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Denormalized deliberately: an append-only history must stay readable exactly as
        // recorded even after a storage location is renamed or retired.
        builder.Property(e => e.StorageLocationPath).HasMaxLength(1000);
        builder.Property(e => e.Reason).HasMaxLength(1000);
    }
}

public sealed class SealEventConfiguration : IEntityTypeConfiguration<SealEvent>
{
    public void Configure(EntityTypeBuilder<SealEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(e => e.Action).HasConversion<int>();
        builder.Property(e => e.PerformedByName).HasMaxLength(256);
        builder.Property(e => e.PurposeOfBreach).HasMaxLength(1000);
        builder.Property(e => e.MfrReference).HasMaxLength(256);
        builder.Property(e => e.DirectingSupervisorName).HasMaxLength(256);
    }
}

public sealed class ExaminationEventConfiguration : IEntityTypeConfiguration<ExaminationEvent>
{
    public void Configure(EntityTypeBuilder<ExaminationEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(e => e.Laboratory).HasMaxLength(256);
        builder.Property(e => e.ExaminationRequestReference).HasMaxLength(256);
        builder.Property(e => e.ExhibitNumber).HasMaxLength(128);
        builder.Property(e => e.ExtractionDescription).HasMaxLength(2000);
        builder.Property(e => e.ResultReference).HasMaxLength(256);
    }
}

public sealed class StatusEventConfiguration : IEntityTypeConfiguration<StatusEvent>
{
    public void Configure(EntityTypeBuilder<StatusEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(e => e.FromStatus).HasConversion<int>();
        builder.Property(e => e.ToStatus).HasConversion<int>();
        builder.Property(e => e.Reason).HasMaxLength(1000);
    }
}

public sealed class DocumentNumberEventConfiguration : IEntityTypeConfiguration<DocumentNumberEvent>
{
    public void Configure(EntityTypeBuilder<DocumentNumberEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(e => e.DocumentNumber).HasMaxLength(16);
        builder.Property(e => e.PreviousDocumentNumber).HasMaxLength(16);
    }
}

public sealed class CorrectionEventConfiguration : IEntityTypeConfiguration<CorrectionEvent>
{
    public void Configure(EntityTypeBuilder<CorrectionEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(e => e.FieldName).HasMaxLength(128);

        // AR 195-5 2-5b(5) - the original entry must remain readable, so the original value is
        // retained verbatim and at the same length as the field it corrects.
        builder.Property(e => e.OriginalValue).HasMaxLength(4000);
        builder.Property(e => e.CorrectedValue).HasMaxLength(4000);
        builder.Property(e => e.Reason).HasMaxLength(2000);

        // AR 195-5 1-7c(3) - the MFR outlining the error and corrective action.
        builder.Property(e => e.MfrReference).HasMaxLength(256);

        builder.HasOne(e => e.CorrectedEvent)
            .WithMany()
            .HasForeignKey(e => e.CorrectsEventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(e => e.Category).HasConversion<int>().IsRequired();

        // AUD-016 - a correction to a field that names a row carries the identifier as well as
        // the text, so projections built on the identifier move with the correction. Stored as
        // int rather than by name so the persisted value does not depend on enum member ordering
        // or spelling.
        builder.Property(e => e.ReferenceKind).HasConversion<int>().IsRequired();

        builder.HasIndex(e => new { e.ReferenceKind, e.CorrectedReferenceId })
            .HasDatabaseName("IX_ItemEvents_CorrectionReference")
            .HasFilter("[CorrectedReferenceId] IS NOT NULL");

        builder.Ignore(e => e.SatisfiesParagraph1_7c3);
        builder.Ignore(e => e.RequiresParagraph1_7c3Documentation);
        builder.Ignore(e => e.IsReferenceCorrection);
    }
}

public sealed class CustodyPartyConfiguration : IEntityTypeConfiguration<CustodyParty>
{
    public void Configure(EntityTypeBuilder<CustodyParty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CustodyParties");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Kind).HasConversion<int>().IsRequired();
        builder.Property(p => p.DisplayName).HasMaxLength(512).IsRequired();
        builder.Property(p => p.TitleOrGrade).HasMaxLength(128);
        builder.Property(p => p.OrganizationOrAgency).HasMaxLength(256);

        // AR 195-5 2-7e - the registered or other accountable mail number, which the regulation
        // directs be entered in the Received By block of the chain of custody section.
        builder.Property(p => p.AccountableMailNumber).HasMaxLength(128);

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType).HasConversion<int>().IsRequired();
        builder.Property(e => e.ActingUserName).HasMaxLength(256).IsRequired();
        builder.Property(e => e.AffectedRecordType).HasMaxLength(128).IsRequired();
        builder.Property(e => e.AffectedRecordId).HasMaxLength(256);
        builder.Property(e => e.PreviousValue).HasMaxLength(4000);
        builder.Property(e => e.NewValue).HasMaxLength(4000);
        builder.Property(e => e.Reason).HasMaxLength(2000);
        builder.Property(e => e.SourceAddress).HasMaxLength(64);
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        builder.HasIndex(e => e.OccurredAtUtc);
        builder.HasIndex(e => new { e.AffectedRecordType, e.AffectedRecordId });
        builder.HasIndex(e => new { e.ActingUserId, e.OccurredAtUtc });

    }
}

public sealed class SystemConfigurationConfiguration : IEntityTypeConfiguration<SystemConfiguration>
{
    public void Configure(EntityTypeBuilder<SystemConfiguration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SystemConfigurations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.OrganizationName).HasMaxLength(256).IsRequired();
        builder.Property(c => c.AuthoritativeMode).HasConversion<int>().IsRequired();
        builder.Property(c => c.NumberingMode).HasConversion<int>().IsRequired();
        builder.Property(c => c.AutomatedSystemApprovalReference).HasMaxLength(256);
        builder.Property(c => c.AccreditedClassificationLevel).HasMaxLength(128).IsRequired();
        builder.Property(c => c.ConcurrencyStamp).IsConcurrencyToken();

        builder.Ignore(c => c.AuthoritativeRecordNotice);
    }
}
