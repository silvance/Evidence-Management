using Emc.Domain.Common;
using Emc.Domain.Filing;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// The paper DA Form 4137 record on its two axes: what became of the original, and what this
/// room holds. Requirements: FIL-001 .. FIL-014, SUSP-007 (paper), RET-007 (paper).
/// </summary>
public class PhysicalDocumentTests
{
    private const int Room = 7;
    private const int OtherRoom = 8;
    private const int User = 42;
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 14, 0, 0, TimeSpan.Zero);

    private static PhysicalFileContainer Active(int id = 1, int from = 1, int to = 50, int year = 2026, ContainerForm form = ContainerForm.Binder, int room = Room)
    {
        var c = new PhysicalFileContainer(room, PhysicalFileKind.Active4137File, form, $"ACTIVE {from:000}-{year % 100:00} to {to:000}-{year % 100:00}", year, from, to, $"{from:000}-{year % 100:00}", $"{to:000}-{year % 100:00}");
        SetId(c, id);
        return c;
    }

    private static PhysicalFileContainer Folder(PhysicalFileKind kind, int id, int room = Room)
    {
        var c = new PhysicalFileContainer(room, kind, ContainerForm.Folder, kind.ToString());
        SetId(c, id);
        return c;
    }

    private static PhysicalFileContainer Inactive(int id, int year, int month, int room = Room)
    {
        var c = new PhysicalFileContainer(room, PhysicalFileKind.Inactive4137File, ContainerForm.Other, $"INACTIVE {month:00}/{year}", null, null, null, null, null, year, month);
        SetId(c, id);
        return c;
    }

    private static void SetId(Entity e, int id) => typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(e, id);

    private static PhysicalVoucherDocument Filed(PhysicalFileContainer active, int sequence = 7)
    {
        var d = new PhysicalVoucherDocument(voucherId: 1, evidenceRoomId: Room);
        d.FileOriginalInActiveFile(active, sequence, 2026, User, T0);
        return d;
    }

    [Fact]
    public void TheOriginalIsFiledInTheActiveBinderThatCoversItsNumber()
    {
        var active = Active();
        var d = Filed(active);

        Assert.Equal(OriginalDisposition.HeldActive, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.ActiveOriginal, d.RetainedPaperStatus);
        Assert.Equal(active.Id, d.CurrentContainerId);
        Assert.Equal(active.Id, d.HomeActiveContainerId);
        Assert.Equal(1, active.FiledVoucherCount);
        Assert.Single(d.Events);
    }

    [Fact]
    public void AVoucherOutsideTheBindersRangeCannotBeFiledInIt()
    {
        // FIL-012. 051-26 does not belong in "001-26 through 050-26".
        var active = Active(from: 1, to: 50);
        var d = new PhysicalVoucherDocument(1, Room);
        var ex = Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInActiveFile(active, 51, 2026, User, T0));
        Assert.Equal("FIL-012", ex.RequirementId);
        Assert.Equal(0, active.FiledVoucherCount);

        // Nor a number from another year, whatever its sequence.
        Assert.Equal("FIL-012", Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInActiveFile(active, 7, 2025, User, T0)).RequirementId);
        Assert.Equal(OriginalDisposition.NotYetFiled, d.OriginalDisposition);
    }

    [Fact]
    public void AnActiveFileIsAFolderOrBinderWithARangeOfAtMostFifty()
    {
        // FIL-011, FIL-012. AR 195-5 2-4f(1).
        Assert.Equal("FIL-011", Assert.Throws<DomainRuleViolationException>(() => Active(form: ContainerForm.Other)).RequirementId);
        Assert.Equal("FIL-012", Assert.Throws<DomainRuleViolationException>(() => new PhysicalFileContainer(Room, PhysicalFileKind.Active4137File, ContainerForm.Binder, "no range")).RequirementId);
        Assert.Equal("FIL-012", Assert.Throws<DomainRuleViolationException>(() => Active(from: 1, to: 51)).RequirementId);
        Assert.Equal("FIL-012", Assert.Throws<DomainRuleViolationException>(() => Active(from: 10, to: 9)).RequirementId);
        Assert.Equal("FIL-012", Assert.Throws<DomainRuleViolationException>(() => new PhysicalFileContainer(Room, PhysicalFileKind.SuspenseUsacil, ContainerForm.Folder, "x", 2026, 1, 50, "a", "b")).RequirementId);

        // Suspense and inactive files may be anything; the regulation names no form for them.
        _ = new PhysicalFileContainer(Room, PhysicalFileKind.SuspenseUsacil, ContainerForm.Other, "USACIL");
        _ = Inactive(9, 2026, 9);
    }

    [Fact]
    public void AnActiveFileRefusesTheFiftyFirstVoucher_AndBumpsItsStampOnEveryFiling()
    {
        // FIL-002. The count is the container's own, guarded by the concurrency stamp.
        var active = Active(from: 1, to: 50);
        var stamps = new HashSet<Guid> { active.ConcurrencyStamp };
        for (var i = 1; i <= 50; i++)
        {
            active.RecordFiled();
            Assert.True(stamps.Add(active.ConcurrencyStamp), "each filing changes the stamp");
        }

        Assert.Equal(50, active.FiledVoucherCount);
        var ex = Assert.Throws<DomainRuleViolationException>(active.RecordFiled);
        Assert.Equal("FIL-002", ex.RequirementId);
        Assert.Equal(50, active.FiledVoucherCount);

        active.RecordRemoved();
        Assert.Equal(49, active.FiledVoucherCount);
        active.RecordFiled();
        Assert.Equal(50, active.FiledVoucherCount);
    }

    [Fact]
    public void TheOriginalMustBeFiledInThisRoomsActiveFile_NotASuspenseFolderOrAnotherRoom()
    {
        var d = new PhysicalVoucherDocument(1, Room);
        Assert.Equal("FIL-001", Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInActiveFile(Folder(PhysicalFileKind.SuspenseUsacil, 2), 7, 2026, User, T0)).RequirementId);
        Assert.Equal("FIL-001", Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInActiveFile(Active(room: OtherRoom), 7, 2026, User, T0)).RequirementId);
        Assert.Equal(OriginalDisposition.NotYetFiled, d.OriginalDisposition);
    }

    [Fact]
    public void TemporaryReleaseSendsTheOriginalOut_TheBinderNoLongerHoldsIt_TheSuspenseFolderHoldsTheCopy()
    {
        // AR 195-5 2-7b, 2-4f(2). SUSP-007 (paper). The Phase 2 defect: the binder used to keep
        // counting an original that was with USACIL.
        var active = Active();
        var usacil = Folder(PhysicalFileKind.SuspenseUsacil, 2);
        var d = Filed(active);

        d.ReleaseOriginalWithEvidence(active, usacil, User, T0.AddDays(1), "To USACIL");

        Assert.Equal(OriginalDisposition.AccompanyingTemporaryRelease, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.SuspenseCopy, d.RetainedPaperStatus);
        Assert.Equal(usacil.Id, d.CurrentContainerId);
        Assert.Equal(active.Id, d.HomeActiveContainerId);
        Assert.Equal(0, active.FiledVoucherCount);
        Assert.Equal(1, usacil.FiledVoucherCount);
        Assert.True(d.OriginalIsOut);
        Assert.True(d.HoldsCopyOnly);
        Assert.Contains(d.Events, e => e.Kind == PhysicalDocumentEventKind.SuspenseCopyRetained && e.ContainerId == usacil.Id);
    }

    [Fact]
    public void TheSuspenseCopyGoesInTheUsacilOrAdjudicationFolder_NotThePendingDispositionFolder()
    {
        var active = Active();
        var d = Filed(active);
        var pending = Folder(PhysicalFileKind.SuspensePendingDispositionApproval, 3);
        var ex = Assert.Throws<DomainRuleViolationException>(() => d.ReleaseOriginalWithEvidence(active, pending, User, T0));
        Assert.Equal("FIL-005", ex.RequirementId);
        Assert.Equal(1, active.FiledVoucherCount);
    }

    [Fact]
    public void TheOriginalReturnsToItsBinder_AndTheFirstCopyIsFiledWithIt()
    {
        // AR 195-5 2-7b: "The first (suspense) copy, with the chain of custody properly annotated,
        // will be filed with the original DA Form 4137."
        var active = Active();
        var usacil = Folder(PhysicalFileKind.SuspenseUsacil, 2);
        var d = Filed(active);
        d.ReleaseOriginalWithEvidence(active, usacil, User, T0.AddDays(1));

        d.ReturnOriginalToActiveFile(active, usacil, 7, 2026, User, T0.AddDays(20));

        Assert.Equal(OriginalDisposition.HeldActive, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.ActiveOriginal, d.RetainedPaperStatus);
        Assert.Equal(active.Id, d.CurrentContainerId);
        Assert.True(d.SuspenseCopyFiledWithOriginal);
        Assert.Equal(1, active.FiledVoucherCount);
        Assert.Equal(0, usacil.FiledVoucherCount);
        Assert.Contains(d.Events, e => e.Kind == PhysicalDocumentEventKind.SuspenseCopyFiledWithOriginal);

        // Returning into a binder whose range does not cover the number is refused.
        d.ReleaseOriginalWithEvidence(active, usacil, User, T0.AddDays(30));
        var wrong = Active(id: 9, from: 51, to: 100);
        Assert.Equal("FIL-012", Assert.Throws<DomainRuleViolationException>(() => d.ReturnOriginalToActiveFile(wrong, usacil, 7, 2026, User, T0.AddDays(40))).RequirementId);
        Assert.Equal(1, usacil.FiledVoucherCount);
    }

    [Fact]
    public void DispositionApprovalSendsTheOriginalToThePendingFolder_TheEvidenceStays()
    {
        var active = Active();
        var pending = Folder(PhysicalFileKind.SuspensePendingDispositionApproval, 3);
        var d = Filed(active);

        d.SendOriginalForDispositionApproval(active, pending, User, T0.AddDays(1));

        Assert.Equal(OriginalDisposition.SentForDispositionApproval, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.SuspenseCopy, d.RetainedPaperStatus);
        Assert.Equal(pending.Id, d.CurrentContainerId);
        Assert.Equal(0, active.FiledVoucherCount);
    }

    [Fact]
    public void InactiveFilingRequiresTheTwoDashFourHClosureBasis_OrReliefUnderThreeDashThreeC()
    {
        // FIL-006. 2-4h: "after all items ... have been properly disposed". 3-3c: relief
        // "permits the closure of the DA Form 4137". Neither is "every terminal state".
        var active = Active();
        var inactive = Inactive(5, 2026, 10);
        var at = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

        var open = Filed(active);
        Assert.Equal("FIL-006", Assert.Throws<DomainRuleViolationException>(() => open.FileOriginalInactive(inactive, active, VoucherClosureBasis.NotClosed, User, at)).RequirementId);

        var disposed = Filed(active);
        disposed.FileOriginalInactive(inactive, active, VoucherClosureBasis.AllItemsFinallyDisposed, User, at);
        Assert.Equal(OriginalDisposition.FiledInactive, disposed.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.InactiveOriginal, disposed.RetainedPaperStatus);
        Assert.Null(disposed.HomeActiveContainerId);
        Assert.Equal(inactive.Id, disposed.CurrentContainerId);
        Assert.Equal(AccountabilityTime.Normalize(at), disposed.InactiveSinceUtc);
        Assert.Equal(at.AddYears(3), disposed.DestructionEligibleAtUtc);

        var relieved = Filed(active);
        relieved.FileOriginalInactive(inactive, active, VoucherClosureBasis.AllItemsReliefGranted, User, at);
        Assert.Contains("3-3c", relieved.Events[^1].Narrative, StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentTransferCannotBeFiledAsAnOrdinaryInactiveOriginal()
    {
        // Phase 1 regression. Items = PermanentlyTransferred -> generic inactive filing -> "this
        // room keeps the original" was possible. It is not: 2-7g sends the original and duplicate
        // with the evidence; the sending room keeps a COPY (2-4d).
        var active = Active();
        var inactive = Inactive(5, 2026, 10);
        var at = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        var d = Filed(active);

        var ex = Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInactive(inactive, active, VoucherClosureBasis.AllItemsPermanentlyTransferred, User, at));
        Assert.Equal("FIL-006", ex.RequirementId);
        Assert.Contains("2-7g", ex.Message, StringComparison.Ordinal);
        Assert.Equal(OriginalDisposition.HeldActive, d.OriginalDisposition);
        Assert.Equal(1, active.FiledVoucherCount);

        Assert.Equal("FIL-006", Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInactive(inactive, active, VoucherClosureBasis.MixedIncludingPermanentTransfer, User, at)).RequirementId);

        // The transfer path, in turn, needs the transfer basis.
        Assert.Equal("FIL-007", Assert.Throws<DomainRuleViolationException>(() => d.TransferOriginalToGainingRoom(inactive, active, VoucherClosureBasis.AllItemsFinallyDisposed, "TEST GAINING ROOM", User, at)).RequirementId);

        d.TransferOriginalToGainingRoom(inactive, active, VoucherClosureBasis.AllItemsPermanentlyTransferred, "TEST GAINING ROOM", User, at);
        Assert.Equal(OriginalDisposition.TransferredToGainingRoom, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.InactiveCopy, d.RetainedPaperStatus);
        Assert.Equal(CopyRetentionReason.OriginalTransferredToGainingRoom, d.CopyReason);
        Assert.Equal(0, active.FiledVoucherCount);
        Assert.Equal(1, inactive.FiledVoucherCount);
    }

    [Fact]
    public void AnInactiveFileTakesOnlyRecordsInactiveInItsLabelledMonthAndYear()
    {
        // FIL-013. AR 195-5 2-4h: labelled by month and year of the disposition date.
        var active = Active();
        var august2022 = Inactive(5, 2022, 8);
        var d = Filed(active);
        var september2023 = new DateTimeOffset(2023, 9, 4, 9, 0, 0, TimeSpan.Zero);

        var ex = Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInactive(august2022, active, VoucherClosureBasis.AllItemsFinallyDisposed, User, september2023));
        Assert.Equal("FIL-013", ex.RequirementId);
        Assert.Equal(OriginalDisposition.HeldActive, d.OriginalDisposition);
        Assert.Equal(0, august2022.FiledVoucherCount);

        d.FileOriginalInactive(Inactive(6, 2023, 9), active, VoucherClosureBasis.AllItemsFinallyDisposed, User, september2023);
        Assert.Equal(OriginalDisposition.FiledInactive, d.OriginalDisposition);
    }

    [Theory]
    [InlineData(CopyRetentionReason.OriginalInRecordOfTrial, OriginalDisposition.PartOfRecordOfTrial, PhysicalDocumentEventKind.CopyFiledInactiveOriginalInRecordOfTrial)]
    [InlineData(CopyRetentionReason.OriginalWithExternalAgency, OriginalDisposition.WithExternalAgency, PhysicalDocumentEventKind.CopyFiledInactiveOriginalWithExternalAgency)]
    [InlineData(CopyRetentionReason.OriginalUnavailableOther, OriginalDisposition.UnavailableOther, PhysicalDocumentEventKind.CopyFiledOriginalUnavailable)]
    public void ACopyIsFiledInactiveWhenTheOriginalIsUnavailable(CopyRetentionReason reason, OriginalDisposition disposition, PhysicalDocumentEventKind kind)
    {
        // AR 195-5 2-4g. FIL-008.
        var active = Active();
        var usacil = Folder(PhysicalFileKind.SuspenseUsacil, 2);
        var inactive = Inactive(5, 2026, 11);
        var d = Filed(active);
        d.ReleaseOriginalWithEvidence(active, usacil, User, T0.AddDays(1));

        d.FileCopyInactiveBecauseOriginalUnavailable(inactive, usacil, reason, "Entered in the record of trial / retained by the agency (test).", User, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(disposition, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.InactiveCopy, d.RetainedPaperStatus);
        Assert.Equal(reason, d.CopyReason);
        Assert.Equal(inactive.Id, d.CurrentContainerId);
        Assert.Equal(0, usacil.FiledVoucherCount);
        Assert.Contains(d.Events, e => e.Kind == kind);
    }

    [Theory]
    [InlineData(CopyRetentionReason.OriginalTransferredToGainingRoom)]
    [InlineData(CopyRetentionReason.OriginalInRecordOfTrial)]
    [InlineData(CopyRetentionReason.OriginalWithExternalAgency)]
    [InlineData(CopyRetentionReason.OriginalUnavailableOther)]
    public void DestroyingThisRoomsRetainedCopyNeverRewritesWhatBecameOfTheOriginal(CopyRetentionReason reason)
    {
        // FIL-014. The Phase 0 defect: destruction of the sending room's copy three years on used
        // to turn "TransferredToGainingRoom" into "Destroyed". The original was not destroyed here.
        var active = Active();
        var inactive = Inactive(5, 2026, 10);
        var at = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        var d = Filed(active);

        if (reason == CopyRetentionReason.OriginalTransferredToGainingRoom)
        {
            d.TransferOriginalToGainingRoom(inactive, active, VoucherClosureBasis.AllItemsPermanentlyTransferred, "TEST GAINING ROOM", User, at);
        }
        else
        {
            d.FileCopyInactiveBecauseOriginalUnavailable(inactive, active, reason, "test", User, at);
        }

        var dispositionBefore = d.OriginalDisposition;
        Assert.NotEqual(OriginalDisposition.FiledInactive, dispositionBefore);

        d.ConfirmDestruction(inactive, User, at.AddYears(3).AddDays(1), "Shredded; witnessed (test).");

        Assert.Equal(dispositionBefore, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.Destroyed, d.RetainedPaperStatus);
        Assert.Equal(PaperRetentionStatus.DestructionConfirmed, d.RetentionStatusAt(at.AddYears(4)));
        Assert.Null(d.CurrentContainerId);
        Assert.Equal(0, inactive.FiledVoucherCount);
        Assert.All(d.Events.Where(e => e.Kind == PhysicalDocumentEventKind.DestructionConfirmed), e => Assert.Equal(dispositionBefore, e.ResultingOriginalDisposition));
        Assert.DoesNotContain(Enum.GetNames<OriginalDisposition>(), n => n.Contains("Destroy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DestroyingAnInactiveOriginalIsRecordedOnTheRetainedAxis_TheOriginalStaysFiledInactive()
    {
        var active = Active();
        var inactive = Inactive(5, 2026, 10);
        var at = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        var d = Filed(active);
        d.FileOriginalInactive(inactive, active, VoucherClosureBasis.AllItemsFinallyDisposed, User, at);

        Assert.Equal(PaperRetentionStatus.Retain, d.RetentionStatusAt(at.AddYears(3).AddDays(-1)));
        Assert.Equal(PaperRetentionStatus.EligibleForDestruction, d.RetentionStatusAt(at.AddYears(3)));
        Assert.Equal("FIL-009", Assert.Throws<DomainRuleViolationException>(() => d.ConfirmDestruction(inactive, User, at.AddYears(2), "too early")).RequirementId);

        d.ConfirmDestruction(inactive, User, at.AddYears(3), "Shredded (test).");
        Assert.Equal(OriginalDisposition.FiledInactive, d.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.Destroyed, d.RetainedPaperStatus);
        Assert.Equal("FIL-009", Assert.Throws<DomainRuleViolationException>(() => d.ConfirmDestruction(null, User, at.AddYears(4), "again")).RequirementId);
    }

    [Fact]
    public void TheReasonForACopyMustBeOneOfParagraph2_4g()
    {
        var active = Active();
        var inactive = Inactive(5, 2026, 10);
        var d = Filed(active);
        var ex = Assert.Throws<DomainRuleViolationException>(() => d.FileCopyInactiveBecauseOriginalUnavailable(inactive, active, CopyRetentionReason.None, "x", User, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("FIL-008", ex.RequirementId);
        Assert.Equal("FIL-008", Assert.Throws<DomainRuleViolationException>(() => d.FileCopyInactiveBecauseOriginalUnavailable(inactive, active, CopyRetentionReason.OriginalTransferredToGainingRoom, "x", User, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))).RequirementId);
        Assert.Equal(1, active.FiledVoucherCount);
    }

    [Fact]
    public void AnActiveRecordCannotBeDestroyed()
    {
        var d = Filed(Active());
        Assert.Equal("FIL-009", Assert.Throws<DomainRuleViolationException>(() => d.ConfirmDestruction(null, User, T0.AddYears(5), "x")).RequirementId);
    }

    [Fact]
    public void AnOriginalThatIsOutCannotBeReleasedAgainOrFiledActive()
    {
        var active = Active();
        var usacil = Folder(PhysicalFileKind.SuspenseUsacil, 2);
        var d = Filed(active);
        d.ReleaseOriginalWithEvidence(active, usacil, User, T0);
        Assert.Equal("FIL-005", Assert.Throws<DomainRuleViolationException>(() => d.ReleaseOriginalWithEvidence(active, usacil, User, T0)).RequirementId);
        Assert.Equal("FIL-004", Assert.Throws<DomainRuleViolationException>(() => d.FileOriginalInActiveFile(active, 7, 2026, User, T0)).RequirementId);
    }

    [Fact]
    public void EveryEventCarriesWhoWhenAndBothResultingStatuses()
    {
        var active = Active();
        var d = Filed(active);
        var e = Assert.Single(d.Events);
        Assert.Equal(User, e.RecordedByUserId);
        Assert.Equal(T0, e.OccurredAtUtc);
        Assert.Equal(OriginalDisposition.HeldActive, e.ResultingOriginalDisposition);
        Assert.Equal(RetainedPaperStatus.ActiveOriginal, e.ResultingRetainedPaperStatus);
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(PhysicalVoucherDocumentEvent)));
    }
}
