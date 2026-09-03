using Emc.Application.Cases;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// AR 195-5 para 2-3g end to end: the custodian returns a submitted DA Form 4137, the submitting
/// agent corrects it and resubmits, the custodian accepts. Requirements: VCH-017 .. VCH-021.
/// </summary>
public class CustodianReviewTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<(int VoucherId, int ItemId)> SubmittedVoucherAsync()
    {
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            $"CASE-{Guid.NewGuid():N}"[..20], "Review test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var itemResult = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One Samsung SM-S921U cellular telephone", "1",
            "R58N30XXXXX", "356938035643809", false, false, false, null));

        var submit = await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);
        Assert.True(submit.Succeeded, submit.Error);

        return (voucherResult.Value, itemResult.Value);
    }

    private async Task<(int VoucherId, int ItemId)> ReturnedVoucherAsync()
    {
        var (voucherId, itemId) = await SubmittedVoucherAsync();

        _harness.SignInAsCustodian();
        var result = await _harness.Vouchers.ReturnForCorrectionAsync(
            new ReturnVoucherForCorrectionRequest(voucherId, "Serial number on item 1 does not match the device."));

        Assert.True(result.Succeeded, result.Error);
        return (voucherId, itemId);
    }

    [Fact]
    public async Task TheCustodianReturnsTheVoucherAndTheItemsGoBackToTheAgent()
    {
        // VCH-017. The return is on the voucher's review record AND on each item's own history,
        // with the custodian's reason.
        var (voucherId, itemId) = await ReturnedVoucherAsync();

        var view = await _harness.Reads.GetVoucherAsync(voucherId);
        Assert.Equal(VoucherReviewStage.ReturnedToSubmittingAgentForCorrection, view!.ReviewStage);
        Assert.Equal(VoucherDerivedStatus.ReturnedForCorrection, view.DerivedStatus);
        Assert.True(view.AllowsItemEditing);

        var returned = view.ReviewActions!.Last();
        Assert.Equal(VoucherReviewActionKind.ReturnedForCorrection, returned.Kind);
        Assert.Contains("Serial number", returned.Narrative!, StringComparison.Ordinal);

        var item = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        Assert.Equal(AccountabilityStatus.Acquired, item.AccountabilityStatus);

        var history = await _harness.History.GetAsync(itemId);
        var lastStatus = history!.History.Where(r => r.Kind == ItemEventKind.Status).Last();
        Assert.Contains("2-3g", lastStatus.Summary, StringComparison.Ordinal);
        Assert.Contains("Serial number", lastStatus.Summary, StringComparison.Ordinal);
        Assert.True(history.ChainVerification.IsIntact);
    }

    [Fact]
    public async Task AnAgentCannotReturnAVoucher()
    {
        // Returning the form is the custodian's act (2-3g), and needs an active appointment.
        var (voucherId, _) = await SubmittedVoucherAsync();

        _harness.SignInAsAgent();
        var result = await _harness.Vouchers.ReturnForCorrectionAsync(
            new ReturnVoucherForCorrectionRequest(voucherId, "errors"));

        Assert.False(result.Succeeded);

        _harness.SignInAsUnappointedCustodian();
        var unappointed = await _harness.Vouchers.ReturnForCorrectionAsync(
            new ReturnVoucherForCorrectionRequest(voucherId, "errors"));

        Assert.False(unappointed.Succeeded);
        Assert.Equal("IAM-005", unappointed.RequirementId);
    }

    [Fact]
    public async Task TheSubmittingAgentEditsCorrectsAndResubmits_AndTheCustodianAccepts()
    {
        // VCH-018, VCH-019, VCH-020. The whole 2-3g round trip through the services.
        var (voucherId, itemId) = await ReturnedVoucherAsync();

        _harness.SignInAsAgent();

        // The agent fixes the item while the voucher is back with them.
        var edit = await _harness.Vouchers.UpdateItemAsync(new UpdateItemRequest(
            itemId, "One Samsung SM-S921U cellular telephone", "1",
            "R58N30YYYYY", "356938035643809", false, false, false, null));
        Assert.True(edit.Succeeded, edit.Error);

        var corrected = await _harness.Vouchers.RecordAgentCorrectionAsync(new RecordAgentCorrectionRequest(
            voucherId, "Serial number on item 1 corrected to R58N30YYYYY.", true));
        Assert.True(corrected.Succeeded, corrected.Error);

        // Once the correction is recorded the form is closed for editing again.
        var lateEdit = await _harness.Vouchers.UpdateItemAsync(new UpdateItemRequest(
            itemId, "Changed again", "1", null, null, false, false, false, null));
        Assert.False(lateEdit.Succeeded);

        _harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var resubmitted = await _harness.Vouchers.ResubmitForCustodianIntakeAsync(voucherId);
        Assert.True(resubmitted.Succeeded, resubmitted.Error);

        var item = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        Assert.Equal(AccountabilityStatus.AwaitingCustodian, item.AccountabilityStatus);
        Assert.Equal("R58N30YYYYY", item.SerialNumber);

        _harness.SignInAsCustodian();
        var accepted = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherId, "003-26", true, _harness.Clock.UtcNow));
        Assert.True(accepted.Succeeded, accepted.Error);

        var view = await _harness.Reads.GetVoucherAsync(voucherId);
        Assert.Equal(VoucherReviewStage.AcceptedByCustodian, view!.ReviewStage);
        Assert.Equal(
            new[]
            {
                VoucherReviewActionKind.Submitted,
                VoucherReviewActionKind.ReturnedForCorrection,
                VoucherReviewActionKind.CorrectedBySubmittingAgent,
                VoucherReviewActionKind.Resubmitted,
                VoucherReviewActionKind.Accepted
            },
            view.ReviewActions!.Select(a => a.Kind).ToArray());

        var correction = view.ReviewActions!.Single(a => a.Kind == VoucherReviewActionKind.CorrectedBySubmittingAgent);
        Assert.True(correction.PaperFormCorrectedAndInitialedAttested);
        Assert.Equal(_harness.AgentPrintedNameAndGrade, correction.ActorName);

        // The item's own history shows the whole trip and the chain is intact.
        var history = await _harness.History.GetAsync(itemId);
        var statuses = history!.History.Where(r => r.Kind == ItemEventKind.Status).Select(r => r.Summary).ToList();
        Assert.Contains(statuses, s => s.Contains("AwaitingCustodian → Acquired", StringComparison.Ordinal));
        Assert.Contains(statuses, s => s.Contains("Acquired → AwaitingCustodian", StringComparison.Ordinal));
        Assert.Contains(statuses, s => s.Contains("AwaitingCustodian → InEvidenceRoom", StringComparison.Ordinal));
        Assert.True(history.ChainVerification.IsIntact);
    }

    [Fact]
    public async Task AnotherAgentInTheSameRoomCannotRecordTheCorrection()
    {
        // VCH-018. 2-3g names "the submitting" agent. Holding the Agent role in the room is not
        // enough; the check is on identity, in the domain.
        var (voucherId, _) = await ReturnedVoucherAsync();

        _harness.SignInAsSecondAgent();
        var result = await _harness.Vouchers.RecordAgentCorrectionAsync(new RecordAgentCorrectionRequest(
            voucherId, "I fixed it", true));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-018", result.RequirementId);

        var resubmit = await _harness.Vouchers.ResubmitForCustodianIntakeAsync(voucherId);
        Assert.False(resubmit.Succeeded);
    }

    [Fact]
    public async Task TheCorrectionIsRefusedWithoutTheInitialingAttestation()
    {
        // VCH-019. The application records that the paper form was corrected and initialed; it
        // does not stand in for the initials.
        var (voucherId, _) = await ReturnedVoucherAsync();

        _harness.SignInAsAgent();
        var result = await _harness.Vouchers.RecordAgentCorrectionAsync(new RecordAgentCorrectionRequest(
            voucherId, "fixed", PaperFormCorrectedAndInitialedAttested: false));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-019", result.RequirementId);
    }

    [Fact]
    public async Task ADocumentNumberCannotBeRecordedWhileTheVoucherIsWithTheAgent()
    {
        var (voucherId, _) = await ReturnedVoucherAsync();

        _harness.SignInAsCustodian();
        var result = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherId, "004-26", true, _harness.Clock.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-006", result.RequirementId);
    }

    [Fact]
    public async Task AnAcceptedVoucherCannotBeReturned()
    {
        // VCH-017. After 2-4c acceptance an incorrect entry is a 1-7c(3) matter for the
        // custodian, with an MFR - not a return to the agent.
        var (voucherId, _) = await SubmittedVoucherAsync();

        _harness.SignInAsCustodian();
        var accepted = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherId, "005-26", true, _harness.Clock.UtcNow));
        Assert.True(accepted.Succeeded, accepted.Error);

        var result = await _harness.Vouchers.ReturnForCorrectionAsync(
            new ReturnVoucherForCorrectionRequest(voucherId, "errors"));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-017", result.RequirementId);
    }

    [Fact]
    public async Task AnItemAlreadyOnTheRecordCannotBeDeletedFromAReturnedVoucher()
    {
        // VCH-021. A submitted line is on the record - revision 1 shows it, its history shows the
        // submission and return. It is withdrawn (below), not deleted. This is a 2-3g form-review
        // rule; it does not rest on the ledger-correction procedure of 2-5b(5).
        var (_, itemId) = await ReturnedVoucherAsync();

        _harness.SignInAsAgent();
        var result = await _harness.Vouchers.RemoveItemAsync(itemId);

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-021", result.RequirementId);
        Assert.DoesNotContain("2-5b", result.Error!, StringComparison.Ordinal);
        Assert.Contains("withdraw", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALineEnteredInErrorIsWithdrawn_KeptInHistory_AndNeverAccepted()
    {
        // VCH-025 / VCH-026, end to end. Two lines submitted; the custodian returns the form;
        // the agent withdraws the erroneous line; the corrected form is resubmitted and accepted
        // with ONE item. The withdrawn line stays on revision 1 and in its own history, receives
        // no document number and no evidence-room state.
        var (voucherId, firstItemId) = await SubmittedVoucherAsync();

        _harness.SignInAsAgent();
        var second = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherId, "One item entered against the wrong case", "1", null, null, false, false, false, null));
        // (added before submission in the helper? no - the helper submitted already, so re-drive:)
        Assert.False(second.Succeeded); // cannot add to a submitted form

        _harness.SignInAsCustodian();
        Assert.True((await _harness.Vouchers.ReturnForCorrectionAsync(
            new ReturnVoucherForCorrectionRequest(voucherId, "Item 1 was seized under another case; add the correct item."))).Succeeded);

        _harness.SignInAsAgent();
        var added = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherId, "One USB flash drive, black", "1", null, null, false, false, false, null));
        Assert.True(added.Succeeded, added.Error);

        var withdrawn = await _harness.Vouchers.WithdrawItemLineAsync(new WithdrawItemLineRequest(
            firstItemId, "Entered against the wrong case; no such item was seized under this case.", true));
        Assert.True(withdrawn.Succeeded, withdrawn.Error);

        Assert.True((await _harness.Vouchers.RecordAgentCorrectionAsync(
            new RecordAgentCorrectionRequest(voucherId, "Withdrew line 1; added the correct item as line 2.", true))).Succeeded);
        Assert.True((await _harness.Vouchers.ResubmitForCustodianIntakeAsync(voucherId)).Succeeded);

        _harness.SignInAsCustodian();
        var accepted = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherId, "009-26", true, _harness.Clock.UtcNow));
        Assert.True(accepted.Succeeded, accepted.Error);

        var view = await _harness.Reads.GetVoucherAsync(voucherId);

        // Current form: line 2 only, and it is the LAST ITEM; line 1 shown withdrawn.
        var line1 = view!.Items.Single(i => i.Id == firstItemId);
        var line2 = view.Items.Single(i => i.Id == added.Value);
        Assert.True(line1.IsWithdrawnFromForm);
        Assert.False(line1.IsLastItem);
        Assert.Equal(AccountabilityStatus.WithdrawnAsEnteredInError, line1.AccountabilityStatus);
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, line2.AccountabilityStatus);
        Assert.True(line2.IsLastItem);
        Assert.Equal(VoucherDerivedStatus.Active, view.DerivedStatus);

        // Revisions: 1 = the form as first submitted (line 1 only); 2 = the corrected form (line 2).
        Assert.Equal(2, view.FormRevisions!.Count);
        Assert.Equal([1], view.FormRevisions[0].Lines.Select(l => l.LineNumber).ToList());
        Assert.Equal([2], view.FormRevisions[1].Lines.Select(l => l.LineNumber).ToList());

        // The withdrawn line got no document number and no evidence-room state, and its history
        // shows why.
        var history = await _harness.History.GetAsync(firstItemId);
        Assert.DoesNotContain(history!.History, r => r.Kind == ItemEventKind.DocumentNumber);
        Assert.Contains(history.History, r => r.Kind == ItemEventKind.Status && r.Summary.Contains("WithdrawnAsEnteredInError", StringComparison.Ordinal));
        Assert.True(history.ChainVerification.IsIntact);

        // The review record shows who withdrew what.
        Assert.Contains(view.ReviewActions!, a => a.Kind == VoucherReviewActionKind.LineWithdrawn && a.ActorName == _harness.AgentPrintedNameAndGrade);
    }

    [Fact]
    public async Task APhysicalItemCannotBeDroppedThroughLineWithdrawal()
    {
        var (_, itemId) = await ReturnedVoucherAsync();

        _harness.SignInAsAgent();
        var result = await _harness.Vouchers.WithdrawItemLineAsync(new WithdrawItemLineRequest(
            itemId, "We decided not to keep it.", AttestsNoPhysicalItemExists: false));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-026", result.RequirementId);
        Assert.Contains("2-8", result.Error!, StringComparison.Ordinal);

        var item = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        Assert.Equal(AccountabilityStatus.Acquired, item.AccountabilityStatus);
    }

    [Fact]
    public async Task AnotherAgentCannotWithdrawALine()
    {
        var (_, itemId) = await ReturnedVoucherAsync();

        _harness.SignInAsSecondAgent();
        var result = await _harness.Vouchers.WithdrawItemLineAsync(new WithdrawItemLineRequest(itemId, "dup", true));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-026", result.RequirementId);
    }

    [Fact]
    public async Task AnItemAddedDuringCorrectionJoinsTheResubmission()
    {
        var (voucherId, _) = await ReturnedVoucherAsync();

        _harness.SignInAsAgent();
        var added = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherId, "One USB flash drive, black", "1", null, null, false, false, false, null));
        Assert.True(added.Succeeded, added.Error);

        Assert.True((await _harness.Vouchers.RecordAgentCorrectionAsync(
            new RecordAgentCorrectionRequest(voucherId, "Added omitted item 2.", true))).Succeeded);
        Assert.True((await _harness.Vouchers.ResubmitForCustodianIntakeAsync(voucherId)).Succeeded);

        var newItem = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == added.Value);
        Assert.Equal(AccountabilityStatus.AwaitingCustodian, newItem.AccountabilityStatus);

        var history = await _harness.History.GetAsync(added.Value);
        Assert.True(history!.ChainVerification.IsIntact);
        Assert.Equal(2, history.History.Count(r => r.Kind == ItemEventKind.Status));
    }
}
