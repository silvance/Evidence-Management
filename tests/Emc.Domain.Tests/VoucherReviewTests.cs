using Emc.Domain.Cases;
using Emc.Domain.Common;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 para 2-3g: "Evidence custodians will review the DA Form 4137 submitted with evidence
/// and have the submitting DALEO or Army CI agent correct and initial all errors."
///
/// A workflow between two people about a FORM, before acceptance under 2-4c. Kept apart from the
/// 1-7c(3) path, which governs an incorrect entry found in an accepted record.
///
/// Requirements: VCH-017 .. VCH-021.
/// </summary>
public class VoucherReviewTests
{
    private const int Agent = 11;
    private const int OtherAgent = 12;
    private const int Custodian = 21;

    private static EvidenceVoucher Submitted()
    {
        var voucher = TestData.NewDraftVoucher();
        voucher.AddSimpleItem("One item");
        voucher.SubmitForCustodianIntake(Agent, TestData.Now);
        return voucher;
    }

    private static EvidenceVoucher Returned()
    {
        var voucher = Submitted();
        voucher.ReturnForCorrection(Custodian, "Serial number on item 1 does not match the item.", TestData.Now.AddHours(1));
        return voucher;
    }

    [Fact]
    public void SubmissionOpensTheReview()
    {
        var voucher = Submitted();

        Assert.Equal(VoucherReviewStage.SubmittedForCustodianReview, voucher.ReviewStage);
        Assert.True(voucher.IsSubmitted);
        Assert.False(voucher.AllowsItemEditing);
        Assert.Equal(Agent, voucher.SubmittedByUserId);

        var action = Assert.Single(voucher.ReviewActions);
        Assert.Equal(VoucherReviewActionKind.Submitted, action.Kind);
        Assert.Equal(Agent, action.ActorUserId);
    }

    [Fact]
    public void TheCustodianReturnsTheFormStatingWhatIsWrong()
    {
        // VCH-017. The return records WHAT the custodian identified - the agent must know what to
        // fix, and the record must show why acceptance waited.
        var voucher = Returned();

        Assert.Equal(VoucherReviewStage.ReturnedToSubmittingAgentForCorrection, voucher.ReviewStage);
        Assert.Equal(VoucherDerivedStatus.ReturnedForCorrection, voucher.DerivedStatus);
        Assert.False(voucher.IsSubmitted);

        var action = voucher.ReviewActions.Last();
        Assert.Equal(VoucherReviewActionKind.ReturnedForCorrection, action.Kind);
        Assert.Equal(Custodian, action.ActorUserId);
        Assert.Contains("Serial number", action.Narrative!, StringComparison.Ordinal);
    }

    [Fact]
    public void AReturnMustStateTheErrorsIdentified()
    {
        var voucher = Submitted();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.ReturnForCorrection(Custodian, "   ", TestData.Now));

        Assert.Equal("VCH-017", ex.RequirementId);
    }

    [Fact]
    public void AReturnedVoucherIsEditableAgain()
    {
        // 2-3g has the agent correct the form. While it is back with the agent, its items are
        // editable on the same terms as a draft; this is what "correct" means for the companion.
        var voucher = Returned();

        Assert.True(voucher.AllowsItemEditing);

        voucher.Items[0].UpdateDetails(
            "One item, corrected", "1", "SN-CORRECTED", null, false, false, false, null);

        Assert.Equal("One item, corrected", voucher.Items[0].Description);
    }

    [Fact]
    public void ADraftCannotBeReturned()
    {
        var voucher = TestData.NewDraftVoucher();
        voucher.AddSimpleItem("One item");

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.ReturnForCorrection(Custodian, "errors", TestData.Now));

        Assert.Equal("VCH-017", ex.RequirementId);
    }

    [Fact]
    public void AnAcceptedVoucherCannotBeReturned_ThatIsAParagraph1_7c3Matter()
    {
        // VCH-017. Once the custodian has received the evidence and assigned the number (2-4c)
        // the form is part of the accountability record. An error in it is the custodian's to
        // correct under 1-7c(3) with an MFR - not something to hand back to the agent.
        var voucher = Submitted();
        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("001-26"), Custodian, TestData.Now.AddHours(1), true);

        Assert.Equal(VoucherReviewStage.AcceptedByCustodian, voucher.ReviewStage);

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.ReturnForCorrection(Custodian, "errors", TestData.Now.AddHours(2)));

        Assert.Equal("VCH-017", ex.RequirementId);
        Assert.Contains("1-7c(3)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheSubmittingAgentRecordsTheCorrection()
    {
        // VCH-018. 2-3g: "the submitting DALEO or Army CI agent". Not any agent, and not the
        // custodian.
        var voucher = Returned();

        foreach (var someoneElse in new[] { OtherAgent, Custodian })
        {
            var ex = Assert.Throws<DomainRuleViolationException>(
                () => voucher.RecordCorrectionBySubmittingAgent(
                    someoneElse, "fixed the serial number", true, TestData.Now.AddHours(2)));

            Assert.Equal("VCH-018", ex.RequirementId);
        }

        Assert.Equal(VoucherReviewStage.ReturnedToSubmittingAgentForCorrection, voucher.ReviewStage);
    }

    [Fact]
    public void TheCorrectionRecordsWhatWasCorrectedAndTheAttestation()
    {
        var voucher = Returned();

        voucher.RecordCorrectionBySubmittingAgent(
            Agent, "Serial number on item 1 corrected to match the item.", true, TestData.Now.AddHours(2));

        Assert.Equal(VoucherReviewStage.CorrectedBySubmittingAgent, voucher.ReviewStage);
        Assert.False(voucher.AllowsItemEditing);

        var action = voucher.ReviewActions.Last();
        Assert.Equal(VoucherReviewActionKind.CorrectedBySubmittingAgent, action.Kind);
        Assert.Equal(Agent, action.ActorUserId);
        Assert.True(action.PaperFormCorrectedAndInitialedAttested);
        Assert.Contains("corrected to match", action.Narrative!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentMustAttestThatThePaperFormWasCorrectedAndInitialed()
    {
        // VCH-019. 2-3g: "correct AND INITIAL". EMC records the attestation that the paper form
        // was initialed; it does not and cannot supply the initials (AUD-013).
        var voucher = Returned();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.RecordCorrectionBySubmittingAgent(
                Agent, "fixed", paperFormCorrectedAndInitialedAttested: false, TestData.Now.AddHours(2)));

        Assert.Equal("VCH-019", ex.RequirementId);
        Assert.Contains("initial", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResubmissionRequiresARecordedCorrection()
    {
        var voucher = Returned();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.Resubmit(Agent, TestData.Now.AddHours(2)));

        Assert.Equal("VCH-020", ex.RequirementId);
    }

    [Fact]
    public void OnlyTheSubmittingAgentResubmits()
    {
        var voucher = Returned();
        voucher.RecordCorrectionBySubmittingAgent(Agent, "fixed", true, TestData.Now.AddHours(2));

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.Resubmit(OtherAgent, TestData.Now.AddHours(3)));

        Assert.Equal("VCH-020", ex.RequirementId);
    }

    [Fact]
    public void TheFullReviewRoundTripIsOnTheRecordInOrder()
    {
        // VCH-020. Submitted -> Returned -> Corrected -> Resubmitted -> Accepted, each with its
        // actor and time.
        var voucher = Returned();
        voucher.RecordCorrectionBySubmittingAgent(Agent, "fixed", true, TestData.Now.AddHours(2));
        voucher.Resubmit(Agent, TestData.Now.AddHours(3));

        Assert.Equal(VoucherReviewStage.ResubmittedForCustodianReview, voucher.ReviewStage);
        Assert.True(voucher.IsSubmitted);
        Assert.Equal(TestData.Now.AddHours(3), voucher.SubmittedAtUtc);

        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("002-26"), Custodian, TestData.Now.AddHours(4), true);

        Assert.Equal(VoucherReviewStage.AcceptedByCustodian, voucher.ReviewStage);

        Assert.Equal(
            new[]
            {
                VoucherReviewActionKind.Submitted,
                VoucherReviewActionKind.ReturnedForCorrection,
                VoucherReviewActionKind.CorrectedBySubmittingAgent,
                VoucherReviewActionKind.Resubmitted,
                VoucherReviewActionKind.Accepted
            },
            voucher.ReviewActions.Select(a => a.Kind).ToArray());

        Assert.Equal(new[] { Agent, Custodian, Agent, Agent, Custodian },
            voucher.ReviewActions.Select(a => a.ActorUserId).ToArray());
    }

    [Fact]
    public void ADocumentNumberCannotBeRecordedWhileTheFormIsWithTheAgent()
    {
        // 2-4c ties the number to RECEIPT of the form. A form the custodian has handed back is
        // not received.
        var voucher = Returned();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.RecordOfficialDocumentNumber(
                EvidenceDocumentNumber.Parse("001-26"), Custodian, TestData.Now.AddHours(2), true));

        Assert.Equal("VCH-006", ex.RequirementId);
        Assert.Contains("2-3g", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReturnedVoucherCannotBeSubmittedAsIfNew()
    {
        var voucher = Returned();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.SubmitForCustodianIntake(Agent, TestData.Now.AddHours(2)));

        Assert.Equal("VCH-010", ex.RequirementId);
        Assert.Contains("resubmit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASecondDocumentNumberDoesNotReopenOrDuplicateTheAcceptance()
    {
        // 2-7g permanent transfer assigns a new number to an accepted voucher. That is not a
        // second acceptance.
        var voucher = Submitted();
        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("001-26"), Custodian, TestData.Now.AddHours(1), true);
        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Parse("044-26"), Custodian, TestData.Now.AddDays(30), true,
            supersessionReason: "Permanent transfer to the receiving evidence room (2-7g).");

        Assert.Single(voucher.ReviewActions, a => a.Kind == VoucherReviewActionKind.Accepted);
        Assert.Equal(VoucherReviewStage.AcceptedByCustodian, voucher.ReviewStage);
    }
}
