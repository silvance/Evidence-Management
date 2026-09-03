using Emc.Domain.Common;
using Emc.Domain.Filing;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// The PHYSICAL DA Form 4137: filing (2-4d, 2-4f), suspense (2-4f(2), 2-4f(3)), inactive filing
/// and the three-year clock (2-4h), copy-only cases (2-4g), permanent transfer (2-7g).
/// Requirements: FIL-001 .. FIL-009.
/// </summary>
public class PhysicalDocumentTests
{
    private const int Room = 1;
    private const int Custodian = 21;
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 13, 15, 0, TimeSpan.Zero);

    private static PhysicalFileContainer Active(string label = "ACTIVE 001-26 to 050-26")
        => new(Room, PhysicalFileKind.Active4137File, ContainerForm.Binder, label, "001-26", "050-26");

    private static PhysicalFileContainer Suspense(PhysicalFileKind kind)
        => new(Room, kind, ContainerForm.Folder, kind.ToString());

    private static PhysicalFileContainer Inactive(int year = 2026, int month = 9)
        => new(Room, PhysicalFileKind.Inactive4137File, ContainerForm.Folder, "INACTIVE", dispositionYear: year, dispositionMonth: month);

    private static PhysicalVoucherDocument FiledActive(PhysicalFileContainer? active = null)
    {
        var document = new PhysicalVoucherDocument(voucherId: 10, evidenceRoomId: Room);
        document.FileOriginalInActiveFile(active ?? Active(), 0, Custodian, T0);
        return document;
    }

    [Fact]
    public void TheOriginalIsFiledInAnActiveFile()
    {
        var document = FiledActive();

        Assert.Equal(PhysicalOriginalStatus.FiledActive, document.OriginalStatus);
        Assert.True(document.OriginalHeldHere);
        Assert.False(document.IsInactive);
        Assert.Equal(PaperRetentionStatus.Retain, document.RetentionStatusAt(T0.AddYears(10)));
        Assert.Equal(PhysicalDocumentEventKind.OriginalFiledActive, Assert.Single(document.Events).Kind);
    }

    [Fact]
    public void AnActiveFileRefusesTheFiftyFirstVoucher()
    {
        // FIL-002 [REG] 2-4f(1).
        var active = Active();
        var document = new PhysicalVoucherDocument(11, Room);

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => document.FileOriginalInActiveFile(active, currentlyFiledInContainer: 50, Custodian, T0));

        Assert.Equal("FIL-002", ex.RequirementId);
        Assert.Contains("50", ex.Message, StringComparison.Ordinal);
        Assert.Equal(PhysicalOriginalStatus.NotYetFiled, document.OriginalStatus);

        // Forty-nine is fine.
        new PhysicalVoucherDocument(12, Room).FileOriginalInActiveFile(active, 49, Custodian, T0);
    }

    [Fact]
    public void TheOriginalMustBeFiledInThisRoomsActiveFile_NotASuspenseFolderOrAnotherRoom()
    {
        var document = new PhysicalVoucherDocument(10, Room);

        Assert.Equal("FIL-001", Assert.Throws<DomainRuleViolationException>(
            () => document.FileOriginalInActiveFile(Suspense(PhysicalFileKind.SuspenseUsacil), 0, Custodian, T0)).RequirementId);

        var otherRoom = new PhysicalFileContainer(Room + 1, PhysicalFileKind.Active4137File, ContainerForm.Folder, "OTHER");
        Assert.Equal("FIL-001", Assert.Throws<DomainRuleViolationException>(
            () => document.FileOriginalInActiveFile(otherRoom, 0, Custodian, T0)).RequirementId);
    }

    [Fact]
    public void AnInactiveFileIsLabeledByDispositionMonthAndYear()
    {
        // FIL-003 [REG] 2-4h.
        var inactive = Inactive(2026, 9);
        Assert.Equal("SEP 2026", inactive.DispositionLabel);

        Assert.Equal("FIL-003", Assert.Throws<DomainRuleViolationException>(
            () => new PhysicalFileContainer(Room, PhysicalFileKind.Inactive4137File, ContainerForm.Folder, "INACTIVE")).RequirementId);

        Assert.Equal("FIL-003", Assert.Throws<DomainRuleViolationException>(
            () => new PhysicalFileContainer(Room, PhysicalFileKind.Active4137File, ContainerForm.Folder, "ACTIVE", dispositionYear: 2026, dispositionMonth: 9)).RequirementId);
    }

    [Fact]
    public void TemporaryReleaseSendsTheOriginalAndKeepsASuspenseCopy()
    {
        // 2-4f(2), 2-4f(3)(a)/(b). Two facts, two events.
        var document = FiledActive();
        var usacil = Suspense(PhysicalFileKind.SuspenseUsacil);

        document.ReleaseOriginalWithEvidence(usacil, Custodian, T0.AddDays(1), "DD Form 2922 to USACIL");

        Assert.Equal(PhysicalOriginalStatus.AccompanyingTemporaryRelease, document.OriginalStatus);
        Assert.True(document.OriginalIsOut);
        Assert.Equal(usacil.Id, document.SuspenseCopyContainerId);
        Assert.Equal(
            [PhysicalDocumentEventKind.OriginalFiledActive, PhysicalDocumentEventKind.OriginalAccompaniesTemporaryRelease, PhysicalDocumentEventKind.SuspenseCopyRetained],
            document.Events.Select(e => e.Kind).ToList());

        // The PENDING DISPOSITION APPROVAL folder is not a temporary-release suspense folder.
        var again = FiledActive();
        Assert.Equal("FIL-005", Assert.Throws<DomainRuleViolationException>(
            () => again.ReleaseOriginalWithEvidence(Suspense(PhysicalFileKind.SuspensePendingDispositionApproval), Custodian, T0)).RequirementId);
    }

    [Fact]
    public void TheOriginalReturnsToTheActiveFile()
    {
        var active = Active();
        var document = FiledActive(active);
        document.ReleaseOriginalWithEvidence(Suspense(PhysicalFileKind.SuspenseAdjudication), Custodian, T0.AddDays(1));

        document.ReturnOriginalToActiveFile(active, 30, Custodian, T0.AddDays(10));

        Assert.Equal(PhysicalOriginalStatus.FiledActive, document.OriginalStatus);
        Assert.Null(document.SuspenseCopyContainerId);
        Assert.Equal(active.Id, document.OriginalContainerId);
    }

    [Fact]
    public void DispositionApprovalSendsTheOriginalToThePendingFolder()
    {
        // 2-4f(3)(c), 2-8e(5).
        var document = FiledActive();
        var pending = Suspense(PhysicalFileKind.SuspensePendingDispositionApproval);

        document.SendOriginalForDispositionApproval(pending, Custodian, T0.AddDays(1), "To trial counsel");

        Assert.Equal(PhysicalOriginalStatus.SentForDispositionApproval, document.OriginalStatus);
        Assert.Equal(pending.Id, document.SuspenseCopyContainerId);
    }

    [Fact]
    public void InactiveFilingStartsTheThreeYearClock_ExactlyThreeYears()
    {
        // FIL-006 [REG] 2-4h. The clock is the inactive date and nothing else.
        var document = FiledActive();
        var inactiveAt = new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero);

        document.FileOriginalInactive(Inactive(2026, 9), Custodian, inactiveAt);

        Assert.Equal(PhysicalOriginalStatus.FiledInactive, document.OriginalStatus);
        Assert.Equal(inactiveAt, document.InactiveSinceUtc);
        Assert.Equal(new DateTimeOffset(2029, 9, 30, 15, 0, 0, TimeSpan.Zero), document.DestructionEligibleAtUtc);

        Assert.Equal(PaperRetentionStatus.Retain, document.RetentionStatusAt(inactiveAt.AddYears(3).AddSeconds(-1)));
        Assert.Equal(PaperRetentionStatus.EligibleForDestruction, document.RetentionStatusAt(inactiveAt.AddYears(3)));
    }

    [Fact]
    public void EligibilityDependsOnNothingButTheInactiveDate()
    {
        // FIL-008. The record has no notion of the case at all: no case id, no case status, no
        // preparation or receipt date enters the calculation. Asserted structurally.
        var props = typeof(PhysicalVoucherDocument).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(props, p => p.Contains("Case", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, p => p.Contains("Acquired", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, p => p.Contains("Received", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EligibleIsNotDestroyed_AndDestructionIsConfirmedByAPerson()
    {
        // FIL-009. Nothing happens on the eligibility date. The custodian records the fact of
        // destruction, and cannot record it early.
        var document = FiledActive();
        document.FileOriginalInactive(Inactive(), Custodian, T0);

        Assert.Equal("FIL-009", Assert.Throws<DomainRuleViolationException>(
            () => document.ConfirmDestruction(Custodian, T0.AddYears(2), "shredded")).RequirementId);

        var eligibleAt = T0.AddYears(3);
        Assert.Equal(PaperRetentionStatus.EligibleForDestruction, document.RetentionStatusAt(eligibleAt));
        Assert.NotEqual(PhysicalOriginalStatus.Destroyed, document.OriginalStatus);

        document.ConfirmDestruction(Custodian, eligibleAt.AddDays(3), "Shredded, witnessed by the alternate custodian.");

        Assert.Equal(PhysicalOriginalStatus.Destroyed, document.OriginalStatus);
        Assert.Equal(PaperRetentionStatus.DestructionConfirmed, document.RetentionStatusAt(eligibleAt.AddDays(3)));
        Assert.Equal(PhysicalDocumentEventKind.DestructionConfirmed, document.Events.Last().Kind);

        Assert.Equal("FIL-009", Assert.Throws<DomainRuleViolationException>(
            () => document.ConfirmDestruction(Custodian, eligibleAt.AddDays(4), "again")).RequirementId);
    }

    [Fact]
    public void AnActiveRecordCannotBeDestroyed()
    {
        var document = FiledActive();
        Assert.Equal("FIL-009", Assert.Throws<DomainRuleViolationException>(
            () => document.ConfirmDestruction(Custodian, T0.AddYears(10), "x")).RequirementId);
    }

    [Fact]
    public void PermanentTransferSendsTheOriginalAndFilesTheSendingRoomsCopyInactive()
    {
        // FIL-007 [REG] 2-7g. The sending room's accountability for the paper ends now; the
        // investigation may continue - the record neither knows nor cares.
        var document = FiledActive();

        document.TransferOriginalToGainingRoom(Inactive(), "310th MI Bn Evidence Room", Custodian, T0.AddDays(5));

        Assert.Equal(PhysicalOriginalStatus.TransferredToGainingRoom, document.OriginalStatus);
        Assert.True(document.HoldsCopyOnly);
        Assert.Equal(CopyRetentionReason.OriginalTransferredToGainingRoom, document.CopyReason);
        Assert.Equal(T0.AddDays(5), document.InactiveSinceUtc);
        Assert.Equal(T0.AddDays(5).AddYears(3), document.DestructionEligibleAtUtc);
        Assert.Contains(document.Events, e => e.Kind == PhysicalDocumentEventKind.SendingRoomCopyFiledInactive);
        Assert.Contains(document.Events, e => e.Narrative is not null && e.Narrative.Contains("310th MI Bn", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CopyRetentionReason.OriginalInRecordOfTrial, PhysicalOriginalStatus.PartOfRecordOfTrial, PhysicalDocumentEventKind.CopyFiledInactiveOriginalInRecordOfTrial)]
    [InlineData(CopyRetentionReason.OriginalWithExternalAgency, PhysicalOriginalStatus.WithExternalAgency, PhysicalDocumentEventKind.CopyFiledInactiveOriginalWithExternalAgency)]
    [InlineData(CopyRetentionReason.OriginalUnavailableOther, PhysicalOriginalStatus.UnavailableOther, PhysicalDocumentEventKind.CopyFiledOriginalUnavailable)]
    public void ACopyIsFiledInactiveWhenTheOriginalIsUnavailable(CopyRetentionReason reason, PhysicalOriginalStatus status, PhysicalDocumentEventKind kind)
    {
        // FIL-008 [REG] 2-4g(1)-(3): the copy notes the disposition of the original.
        var document = FiledActive();
        document.ReleaseOriginalWithEvidence(Suspense(PhysicalFileKind.SuspenseAdjudication), Custodian, T0.AddDays(1));

        document.FileCopyInactiveBecauseOriginalUnavailable(Inactive(), reason, "Original retained in the record of trial, US v. TEST.", Custodian, T0.AddDays(30));

        Assert.Equal(status, document.OriginalStatus);
        Assert.True(document.HoldsCopyOnly);
        Assert.Equal(reason, document.CopyReason);
        Assert.Equal(T0.AddDays(30), document.InactiveSinceUtc);
        Assert.Equal(kind, document.Events.Last().Kind);
        Assert.Contains("record of trial", document.Events.Last().Narrative!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReasonForACopyMustBeOneOfParagraph2_4g()
    {
        var document = FiledActive();
        Assert.Equal("FIL-008", Assert.Throws<DomainRuleViolationException>(() => document.FileCopyInactiveBecauseOriginalUnavailable(
            Inactive(), CopyRetentionReason.OriginalTransferredToGainingRoom, "x", Custodian, T0)).RequirementId);
        Assert.Equal("FIL-008", Assert.Throws<DomainRuleViolationException>(() => document.FileCopyInactiveBecauseOriginalUnavailable(
            Inactive(), CopyRetentionReason.OriginalInRecordOfTrial, "   ", Custodian, T0)).RequirementId);
    }

    [Fact]
    public void AnOriginalThatIsOutCannotBeReleasedAgainOrFiledActive()
    {
        var document = FiledActive();
        document.ReleaseOriginalWithEvidence(Suspense(PhysicalFileKind.SuspenseUsacil), Custodian, T0.AddDays(1));

        Assert.Equal("FIL-005", Assert.Throws<DomainRuleViolationException>(
            () => document.ReleaseOriginalWithEvidence(Suspense(PhysicalFileKind.SuspenseUsacil), Custodian, T0.AddDays(2))).RequirementId);
        Assert.Equal("FIL-004", Assert.Throws<DomainRuleViolationException>(
            () => document.FileOriginalInActiveFile(Active(), 0, Custodian, T0.AddDays(2))).RequirementId);
    }

    [Fact]
    public void EveryEventCarriesWhoWhenAndTheResultingStatus()
    {
        var document = FiledActive();
        var e = document.Events.Single();

        Assert.Equal(Custodian, e.RecordedByUserId);
        Assert.Equal(T0, e.OccurredAtUtc);
        Assert.Equal(PhysicalOriginalStatus.FiledActive, e.ResultingOriginalStatus);
    }
}
