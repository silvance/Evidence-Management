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
            EvidenceDocumentNumber.Regulatory("001-26", 2026), Custodian, TestData.Now.AddHours(1), true);

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
            EvidenceDocumentNumber.Regulatory("002-26", 2026), Custodian, TestData.Now.AddHours(4), true);

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
                EvidenceDocumentNumber.Regulatory("001-26", 2026), Custodian, TestData.Now.AddHours(2), true));

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

    private static EvidenceVoucher ReturnedWithTwoLines()
    {
        var voucher = TestData.NewDraftVoucher();
        voucher.AddSimpleItem("One item");
        voucher.AddSimpleItem("One item entered twice by mistake");
        voucher.SubmitForCustodianIntake(Agent, TestData.Now);
        voucher.ReturnForCorrection(Custodian, "Item 2 duplicates item 1.", TestData.Now.AddHours(1));

        // The application service records these transitions as status events; a returned
        // voucher's lines sit in Acquired (AR 195-5 2-3g, 2-1a).
        foreach (var item in voucher.Items)
        {
            item.TransitionTo(AccountabilityStatus.Acquired);
        }

        return voucher;
    }

    [Fact]
    public void EachSubmissionSnapshotsWhatTheFormContained()
    {
        // VCH-025. Revision 1 is the form as first submitted; the corrected form is revision 2.
        // Neither changes afterwards.
        var voucher = ReturnedWithTwoLines();
        var first = Assert.Single(voucher.FormRevisions);

        Assert.Equal(1, first.RevisionNumber);
        Assert.Equal(VoucherFormRevisionKind.InitialSubmission, first.Kind);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal("One item entered twice by mistake", first.Lines[1].Description);

        voucher.Items[0].UpdateDetails("One item, corrected", "1", "SN-2", null, false, false, false, null);
        voucher.RecordCorrectionBySubmittingAgent(Agent, "fixed", true, TestData.Now.AddHours(2));
        voucher.Resubmit(Agent, TestData.Now.AddHours(3));

        Assert.Equal(2, voucher.FormRevisions.Count);
        Assert.Equal("One item", voucher.FormRevisions[0].Lines[0].Description);            // unchanged history
        Assert.Equal("One item, corrected", voucher.FormRevisions[1].Lines[0].Description);  // the corrected form
        Assert.Equal(VoucherFormRevisionKind.Resubmission, voucher.FormRevisions[1].Kind);
    }

    [Fact]
    public void ALineEnteredInErrorIsWithdrawnFromTheCurrentForm_NotDeleted()
    {
        // VCH-026. The corrected form no longer lists the line; the record still does.
        var voucher = ReturnedWithTwoLines();
        var duplicate = voucher.Items[1];

        voucher.WithdrawLineAsEnteredInError(duplicate, Agent, "Duplicate of item 1.", true, TestData.Now.AddHours(2));
        duplicate.TransitionTo(AccountabilityStatus.WithdrawnAsEnteredInError);

        Assert.True(duplicate.IsWithdrawnFromForm);
        Assert.Equal(2, voucher.Items.Count);
        Assert.Single(voucher.CurrentFormLines);
        Assert.Equal(1, voucher.CurrentFormLines.Single().ItemNumber);

        var action = voucher.ReviewActions.Last();
        Assert.Equal(VoucherReviewActionKind.LineWithdrawn, action.Kind);
        Assert.Equal(Agent, action.ActorUserId);
        Assert.Contains("Duplicate", action.Narrative!, StringComparison.Ordinal);

        // Still on revision 1.
        Assert.Contains(voucher.FormRevisions[0].Lines, l => l.LineNumber == 2);

        // And absent from the corrected form's revision.
        voucher.RecordCorrectionBySubmittingAgent(Agent, "Withdrew duplicate line 2.", true, TestData.Now.AddHours(3));
        voucher.Resubmit(Agent, TestData.Now.AddHours(4));
        Assert.Single(voucher.FormRevisions[1].Lines);
    }

    [Fact]
    public void APhysicalItemCannotBeDroppedByWithdrawingItsLine()
    {
        // VCH-026. The escape hatch that must not exist. Without the attestation that no
        // physical item corresponds to the line, the withdrawal is refused and the message
        // points to 2-8.
        var voucher = ReturnedWithTwoLines();

        var ex = Assert.Throws<DomainRuleViolationException>(() => voucher.WithdrawLineAsEnteredInError(
            voucher.Items[1], Agent, "We no longer want to hold this.", attestsNoPhysicalItemExists: false, TestData.Now.AddHours(2)));

        Assert.Equal("VCH-026", ex.RequirementId);
        Assert.Contains("2-8", ex.Message, StringComparison.Ordinal);
        Assert.Empty(voucher.ReviewActions.Where(a => a.Kind == VoucherReviewActionKind.LineWithdrawn));
    }

    [Fact]
    public void OnlyTheSubmittingAgentWithdrawsALine_AndOnlyFromAReturnedForm()
    {
        var voucher = ReturnedWithTwoLines();

        Assert.Equal("VCH-026", Assert.Throws<DomainRuleViolationException>(() => voucher.WithdrawLineAsEnteredInError(
            voucher.Items[1], OtherAgent, "dup", true, TestData.Now)).RequirementId);

        var submitted = Submitted();
        Assert.Equal("VCH-026", Assert.Throws<DomainRuleViolationException>(() => submitted.WithdrawLineAsEnteredInError(
            submitted.Items[0], Agent, "dup", true, TestData.Now)).RequirementId);
    }

    [Fact]
    public void WithdrawingEveryLineLeavesNothingToResubmit()
    {
        var voucher = Submitted();
        voucher.ReturnForCorrection(Custodian, "Nothing on this form was seized.", TestData.Now.AddHours(1));
        var only = voucher.Items[0];
        only.TransitionTo(AccountabilityStatus.Acquired);
        voucher.WithdrawLineAsEnteredInError(only, Agent, "Entered against the wrong case.", true, TestData.Now.AddHours(2));
        only.TransitionTo(AccountabilityStatus.WithdrawnAsEnteredInError);
        voucher.RecordCorrectionBySubmittingAgent(Agent, "withdrew all", true, TestData.Now.AddHours(3));

        Assert.Equal("VCH-011", Assert.Throws<DomainRuleViolationException>(
            () => voucher.Resubmit(Agent, TestData.Now.AddHours(4))).RequirementId);
    }

    [Fact]
    public void NoParagraph1_7c3DocumentationIsDemandedForA2_3gCorrection()
    {
        // A 2-3g correction is the agent fixing a form the custodian has not accepted. It is not a
        // custodian finding an incorrect entry in an accepted record (1-7c(3)) and it is not a
        // ledger entry (2-5b(5)); no MFR and no supervisor notification are part of it.
        var voucher = ReturnedWithTwoLines();
        voucher.RecordCorrectionBySubmittingAgent(Agent, "fixed", true, TestData.Now.AddHours(2));

        var action = voucher.ReviewActions.Last();
        Assert.Equal(VoucherReviewActionKind.CorrectedBySubmittingAgent, action.Kind);
        Assert.DoesNotContain("MFR", action.Narrative ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondDocumentNumberDoesNotReopenOrDuplicateTheAcceptance()
    {
        // 2-7g permanent transfer assigns a new number to an accepted voucher. That is not a
        // second acceptance.
        var voucher = Submitted();
        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Regulatory("001-26", 2026), Custodian, TestData.Now.AddHours(1), true);
        voucher.RecordOfficialDocumentNumber(
            EvidenceDocumentNumber.Regulatory("044-26", 2026), Custodian, TestData.Now.AddDays(30), true,
            supersessionReason: "Permanent transfer to the receiving evidence room (2-7g).");

        Assert.Single(voucher.ReviewActions, a => a.Kind == VoucherReviewActionKind.Accepted);
        Assert.Equal(VoucherReviewStage.AcceptedByCustodian, voucher.ReviewStage);
    }
}
