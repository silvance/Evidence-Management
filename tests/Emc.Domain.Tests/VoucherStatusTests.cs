using Emc.Domain.Cases;
using Emc.Domain.Common;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 para 2-4h and 2-5b(1)(d) - voucher status is derived from item status.
/// Requirements: VCH-007, VCH-008, ITEM-001. Invariants I-05, I-16.
/// </summary>
public class VoucherStatusTests
{
    private static EvidenceVoucher SubmittedVoucher(int itemCount)
    {
        var voucher = TestData.NewDraftVoucher();

        for (var i = 1; i <= itemCount; i++)
        {
            voucher.AddSimpleItem($"Item {i}");
        }

        voucher.SubmitForCustodianIntake(1, TestData.Now);
        return voucher;
    }

    [Fact]
    public void DraftVoucher_IsDraft()
        => Assert.Equal(VoucherDerivedStatus.Draft, TestData.NewDraftVoucher().DerivedStatus);

    [Fact]
    public void SubmittedButUnaccepted_AwaitsCustodianAcceptance()
    {
        var voucher = SubmittedVoucher(2);

        foreach (var item in voucher.Items)
        {
            item.TransitionTo(AccountabilityStatus.Acquired);
            item.TransitionTo(AccountabilityStatus.AwaitingCustodian);
        }

        Assert.Equal(VoucherDerivedStatus.AwaitingCustodianAcceptance, voucher.DerivedStatus);
    }

    [Fact]
    public void PartiallyAccepted_IsReportedAsSuch()
    {
        // Items on one voucher genuinely diverge - AR 195-5 2-5b(1)(d) contemplates different
        // disposition dates per item, and 2-7b contemplates items released to different agencies
        // at the same time. A single voucher status column could not represent this.
        var voucher = SubmittedVoucher(2);

        foreach (var item in voucher.Items)
        {
            item.TransitionTo(AccountabilityStatus.Acquired);
            item.TransitionTo(AccountabilityStatus.AwaitingCustodian);
        }

        voucher.Items[0].TransitionTo(AccountabilityStatus.InEvidenceRoom);

        Assert.Equal(VoucherDerivedStatus.PartiallyAccepted, voucher.DerivedStatus);
    }

    [Fact]
    public void VoucherBecomesInactiveOnlyWhenEveryItemIsTerminal()
    {
        // AR 195-5 2-4h: "After all items of evidence listed on a DA Form 4137 have been properly
        // disposed, the original DA Form 4137 and related documents will be placed in a separate
        // DA Form 4137 file labeled inactive" (VCH-007).
        var voucher = SubmittedVoucher(2);

        foreach (var item in voucher.Items)
        {
            item.TransitionTo(AccountabilityStatus.Acquired);
            item.TransitionTo(AccountabilityStatus.AwaitingCustodian);
            item.TransitionTo(AccountabilityStatus.InEvidenceRoom);
        }

        voucher.Items[0].TransitionTo(AccountabilityStatus.DispositionPending);
        voucher.Items[0].TransitionTo(AccountabilityStatus.Disposed);

        Assert.Equal(VoucherDerivedStatus.Active, voucher.DerivedStatus);

        voucher.Items[1].TransitionTo(AccountabilityStatus.DispositionPending);
        voucher.Items[1].TransitionTo(AccountabilityStatus.Disposed);

        Assert.Equal(VoucherDerivedStatus.Inactive, voucher.DerivedStatus);
    }

    [Fact]
    public void ReliefGranted_ClosesTheVoucherWithoutDisposition()
    {
        // AR 195-5 3-3c: where an inquiry fails to account for evidence, relief for
        // accountability is granted - for CI units, by Army G-2X - and relief "permits the
        // closure of the DA Form 4137". The item was never disposed of; accountability for it was
        // relieved. Merging the two states would misstate the record (LOSS-005).
        var voucher = SubmittedVoucher(1);
        var item = voucher.Items[0];

        item.TransitionTo(AccountabilityStatus.Acquired);
        item.TransitionTo(AccountabilityStatus.AwaitingCustodian);
        item.TransitionTo(AccountabilityStatus.InEvidenceRoom);
        item.TransitionTo(AccountabilityStatus.DiscrepancyReview);
        item.TransitionTo(AccountabilityStatus.Inquiry);
        item.TransitionTo(AccountabilityStatus.ReliefGranted);

        Assert.Equal(VoucherDerivedStatus.Inactive, voucher.DerivedStatus);
        Assert.NotEqual(AccountabilityStatus.Disposed, item.AccountabilityStatus);
    }

    [Fact]
    public void RecordingASecondDocumentNumber_RequiresASupersessionReason()
    {
        // AR 195-5 2-7g: on permanent transfer the receiving custodian enters the receiving
        // room's next number and "the prior document number will be lined through in such a way
        // that it remains legible" (VCH-008, invariant I-05).
        var voucher = SubmittedVoucher(1);

        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("037-26"), 1, TestData.Now, true);

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.RecordOfficialDocumentNumber(
                EvidenceDocumentNumber.Parse("012-27"), 1, TestData.Now, true));

        Assert.Equal("VCH-008", ex.RequirementId);
    }

    [Fact]
    public void SupersededDocumentNumbers_RemainVisible()
    {
        // The digital equivalent of "lined through in such a way that it remains legible"
        // (AR 195-5 2-7g).
        var voucher = SubmittedVoucher(1);

        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("037-26"), 1, TestData.Now, true);

        // A transfer happens later than the original assignment; "current" is the most recent.
        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("012-27"), 1, TestData.Now.AddMonths(4), true,
            "Evidence permanently transferred to 310th MI Bn evidence room (AR 195-5 2-7g).");

        Assert.Equal(2, voucher.DocumentNumberAssignments.Count);
        Assert.Equal("012-27", voucher.CurrentDocumentNumberAssignment!.DocumentNumber);

        // AR 195-5 2-7g - the prior number remains recorded and legible. Nothing was written to
        // the superseded row; the NEW assignment names the one it replaces (AUD-002).
        Assert.Contains(voucher.DocumentNumberAssignments, a => a.DocumentNumber == "037-26");
        Assert.True(voucher.CurrentDocumentNumberAssignment.Supersedes);
    }

    [Fact]
    public void RecordingADocumentNumber_RequiresTheLedgerAttestation()
    {
        // EMC-002, VCH-006. AR 195-5 2-4c assigns the number "by order of precedence from the
        // evidence ledger", and 2-5a requires that ledger to be a bound book absent approval
        // under 2-5c. The attestation is explicit and stored, never inferred from typing.
        var voucher = SubmittedVoucher(1);

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.RecordOfficialDocumentNumber(
                EvidenceDocumentNumber.Parse("037-26"), 1, TestData.Now,
                attestedAssignedInAuthoritativeLedger: false));

        Assert.Equal("VCH-006", ex.RequirementId);
    }

    [Fact]
    public void ADocumentNumberCannotBeRecordedBeforeSubmission()
    {
        // AR 195-5 2-4c ties assignment to receipt of the evidence AND the DA Form 4137 by the
        // custodian. A draft has not been submitted, so there is nothing to receive.
        var voucher = TestData.NewDraftVoucher();
        voucher.AddSimpleItem();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.RecordOfficialDocumentNumber(
                EvidenceDocumentNumber.Parse("037-26"), 1, TestData.Now, true));

        Assert.Equal("VCH-006", ex.RequirementId);
    }
}
