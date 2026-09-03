using Emc.Domain.Cases;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Emc.Infrastructure.Persistence.Configurations;

public sealed class EvidenceRoomConfiguration : IEntityTypeConfiguration<EvidenceRoom>
{
    public void Configure(EntityTypeBuilder<EvidenceRoom> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EvidenceRooms");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(256).IsRequired();
        builder.Property(r => r.OrganizationOrUnit).HasMaxLength(256).IsRequired();
        builder.Property(r => r.TimeZoneId).HasMaxLength(128).IsRequired();
        builder.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
        builder.HasIndex(r => r.Name).IsUnique();
    }
}

public sealed class StorageLocationConfiguration : IEntityTypeConfiguration<StorageLocation>
{
    public void Configure(EntityTypeBuilder<StorageLocation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("StorageLocations");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Name).HasMaxLength(256).IsRequired();
        builder.Property(l => l.Kind).HasConversion<int>().IsRequired();
        builder.Property(l => l.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne(l => l.EvidenceRoom)
            .WithMany()
            .HasForeignKey(l => l.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Parent)
            .WithMany()
            .HasForeignKey(l => l.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.EvidenceRoomId, l.ParentId, l.Name }).IsUnique();
        builder.Ignore(l => l.FullPath);
    }
}

public sealed class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Cases");
        builder.HasKey(c => c.Id);

        // AR 195-5 2-3b - the Army CI case control number.
        builder.Property(c => c.CaseControlNumber).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Title).HasMaxLength(512).IsRequired();
        builder.Property(c => c.Synopsis).HasMaxLength(4000);
        builder.Property(c => c.ClassificationMarking).HasMaxLength(128).IsRequired();
        builder.Property(c => c.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne<EvidenceRoom>()
            .WithMany()
            .HasForeignKey(c => c.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        // Scoped to the evidence room, not global - see open decision DEC-03.
        builder.HasIndex(c => new { c.EvidenceRoomId, c.CaseControlNumber }).IsUnique();

        builder.HasMany(c => c.Vouchers)
            .WithOne(v => v.Case)
            .HasForeignKey(v => v.CaseId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EvidenceVoucherConfiguration : IEntityTypeConfiguration<EvidenceVoucher>
{
    public void Configure(EntityTypeBuilder<EvidenceVoucher> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EvidenceVouchers");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.TemporaryIdentifier).HasMaxLength(32).IsRequired();
        builder.Property(v => v.ReceivingActivity).HasMaxLength(256).IsRequired();
        builder.Property(v => v.ReceivingActivityLocation).HasMaxLength(256).IsRequired();
        builder.Property(v => v.ReceivedFrom).HasMaxLength(512).IsRequired();
        builder.Property(v => v.RequestingOfficeCaseNumber).HasMaxLength(64);
        builder.Property(v => v.Remarks).HasMaxLength(4000);
        builder.Property(v => v.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne<EvidenceRoom>()
            .WithMany()
            .HasForeignKey(v => v.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(v => new { v.EvidenceRoomId, v.TemporaryIdentifier }).IsUnique();

        builder.HasMany(v => v.Items)
            .WithOne(i => i.Voucher)
            .HasForeignKey(i => i.VoucherId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(v => v.DocumentNumberAssignments)
            .WithOne(a => a.Voucher)
            .HasForeignKey(a => a.VoucherId)
            .OnDelete(DeleteBehavior.Restrict);

        // AR 195-5 2-3g - the review stage is the one stored workflow state on the voucher; the
        // review's history is its own append-only table.
        builder.Property(v => v.ReviewStage).HasConversion<int>().IsRequired();

        builder.HasMany(v => v.ReviewActions)
            .WithOne(a => a.Voucher)
            .HasForeignKey(a => a.VoucherId)
            .OnDelete(DeleteBehavior.Restrict);

        // VCH-007, invariant I-16 - voucher status is DERIVED from its items (AR 195-5 2-4h).
        // There is deliberately no status column to drift out of step with reality. The same
        // goes for IsSubmitted, which is derived from ReviewStage.
        builder.Ignore(v => v.DerivedStatus);
        builder.Ignore(v => v.IsSubmitted);
        builder.Ignore(v => v.CurrentDocumentNumberAssignment);
        builder.Ignore(v => v.HasOfficialDocumentNumber);
        builder.Ignore(v => v.DisplayIdentifier);
        builder.Ignore(v => v.AllowsItemEditing);
    }
}

public sealed class VoucherReviewActionConfiguration : IEntityTypeConfiguration<VoucherReviewAction>
{
    public void Configure(EntityTypeBuilder<VoucherReviewAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("VoucherReviewActions");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Kind).HasConversion<int>().IsRequired();
        builder.Property(a => a.ResultingStage).HasConversion<int>().IsRequired();

        // What the custodian identified, or what the agent corrected - free text as an MFR is.
        builder.Property(a => a.Narrative).HasMaxLength(4000);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.VoucherId, a.OccurredAtUtc });
    }
}

public sealed class OfficialDocumentNumberAssignmentConfiguration
    : IEntityTypeConfiguration<OfficialDocumentNumberAssignment>
{
    public void Configure(EntityTypeBuilder<OfficialDocumentNumberAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OfficialDocumentNumberAssignments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.DocumentNumber).HasMaxLength(16).IsRequired();
        builder.Property(a => a.SupersessionReason).HasMaxLength(1000);

        builder.HasOne<EvidenceRoom>()
            .WithMany()
            .HasForeignKey(a => a.EvidenceRoomId)
            .OnDelete(DeleteBehavior.Restrict);

        // Invariant I-04. AR 195-5 2-4c scopes the document-number series to the calendar year,
        // and 2-7g shows it belongs to the EVIDENCE ROOM ("the next document number of the
        // receiving evidence room"). Uniqueness is therefore per (room, year, sequence).
        //
        // UNFILTERED, deliberately. Once a canonical (room, year, sequence) has been recorded,
        // that number is consumed permanently: it identifies a DA Form 4137 that existed and was
        // entered in the evidence ledger. The earlier filtered index excluded superseded rows,
        // which would have allowed a historical number to be reissued to a different voucher and
        // made the ledger cross-reference ambiguous (VCH-011).
        builder.HasIndex(a => new { a.EvidenceRoomId, a.CalendarYear, a.Sequence })
            .HasDatabaseName("UX_DocumentNumber_NeverReusedPerRoomPerYear")
            .IsUnique();

        // AR 195-5 2-7g - the backward supersession reference.
        builder.HasOne<OfficialDocumentNumberAssignment>()
            .WithMany()
            .HasForeignKey(a => a.SupersedesAssignmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.VoucherId);
        builder.Ignore(a => a.Supersedes);
    }
}

public sealed class EvidenceItemConfiguration : IEntityTypeConfiguration<EvidenceItem>
{
    public void Configure(EntityTypeBuilder<EvidenceItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EvidenceItems");
        builder.HasKey(i => i.Id);

        // AR 195-5 2-3d - the Description of Articles block. Generous length because the
        // regulation requires the description to individualize the item to the exclusion of any
        // other item, and continuation pages exist precisely because descriptions run long.
        builder.Property(i => i.Description).HasMaxLength(4000).IsRequired();
        builder.Property(i => i.Quantity).HasMaxLength(256);
        builder.Property(i => i.SerialNumber).HasMaxLength(256);
        builder.Property(i => i.UniqueDeviceIdentifier).HasMaxLength(256);
        builder.Property(i => i.SealDescription).HasMaxLength(1000);
        builder.Property(i => i.CurrencyDenominationBreakdown).HasMaxLength(2000);
        builder.Property(i => i.CurrencyTotalAmount).HasPrecision(18, 2);
        builder.Property(i => i.AccountabilityStatus).HasConversion<int>().IsRequired();
        builder.Property(i => i.LastEventHash).HasMaxLength(64);
        builder.Property(i => i.ConcurrencyStamp).IsConcurrencyToken();

        // Invariant I-01 - item numbers unique within a voucher (AR 195-5 2-3d).
        builder.HasIndex(i => new { i.VoucherId, i.ItemNumber }).IsUnique();

        builder.HasIndex(i => i.SerialNumber);

        builder.HasMany(i => i.Events)
            .WithOne()
            .HasForeignKey(e => e.EvidenceItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(i => i.IsLastItem);
        builder.Ignore(i => i.DescriptionForForm);
        builder.Ignore(i => i.CurrentCustody);
        builder.Ignore(i => i.CurrentCustodyHolder);
        builder.Ignore(i => i.CurrentLocation);
        builder.Ignore(i => i.CurrentLocationPath);
        builder.Ignore(i => i.ChronologicalHistory);
    }
}
