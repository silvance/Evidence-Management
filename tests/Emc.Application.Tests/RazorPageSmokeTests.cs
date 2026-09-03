using Emc.Application.Cases;
using Emc.Application.Items;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Verifies the data the Razor pages render, so the page models are exercised against real
/// query shapes rather than assumed to work. A full WebApplicationFactory host would require
/// Windows Authentication and SQL Server, neither of which exists in CI.
/// </summary>
public class RazorPageSmokeTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task TheItemHistoryViewRendersEveryFieldThePageDisplays()
    {
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0300-2026-CID902-XXXXX", "Page smoke test", "Synopsis", _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var itemResult = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value,
            "One white cotton t-shirt with reddish-brown staining",
            "1", null, null,
            IsPossibleBiohazard: true,
            IsFungible: false,
            IsSealed: true,
            SealDescription: "sealed in a paper sack which was marked for identification"));

        Assert.True(itemResult.Succeeded, itemResult.Error);

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        _harness.SignInAsCustodian();

        await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherResult.Value, "001-26", true, _harness.Clock.UtcNow));

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemResult.Value, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var view = await _harness.History.GetAsync(itemResult.Value);

        Assert.NotNull(view);
        Assert.Equal("001-26", view.VoucherIdentifier);
        Assert.Equal("0300-2026-CID902-XXXXX", view.CaseControlNumber);

        // AR 195-5 para 2-3l - the annotation the page renders (ITEM-007).
        Assert.EndsWith("POSSIBLE BIOHAZARD", view.DescriptionForForm, StringComparison.Ordinal);

        Assert.Equal("Shelf B / Bin 14", view.CurrentLocationPath);
        Assert.True(view.ChainVerification.IsIntact);

        // Every row the page renders has a recorded-by name resolved, not a raw user id.
        Assert.All(view.History, r => Assert.NotEqual("(unknown user)", r.RecordedByName));
        Assert.All(view.History, r => Assert.False(string.IsNullOrWhiteSpace(r.Summary)));
    }

    [Fact]
    public async Task AnAgentSeesNoCustodianControls()
    {
        // The page renders the custodian sections only when authorization allows them. The
        // service checks again on POST regardless, so this is defence in depth rather than the
        // control itself (IAM-002, IAM-011).
        _harness.SignInAsAgent();

        var decision = await _harness.Authorization.AuthorizeAsync(
            Application.Authorization.EmcPermissions.RecordOfficialDocumentNumber,
            _harness.EvidenceRoomId);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task TheSupersededEntryStaysInTheRenderedHistory()
    {
        // AUD-006. AR 195-5 para 2-5b(5): the struck-through entry must still be readable, so the
        // page must still receive it.
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0301-2026-CID902-XXXXX", "Correction render test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var itemResult = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One item", "1", null, null, false, false, false, null));

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        _harness.SignInAsCustodian();

        await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherResult.Value, "002-26", true, _harness.Clock.UtcNow));

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemResult.Value, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var before = await _harness.History.GetAsync(itemResult.Value);
        var locationRow = before!.History.Single(r => r.Kind == Domain.Common.ItemEventKind.Location);

        await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationRow.EventId, nameof(Domain.Events.LocationEvent.StorageLocationPath),
            null, "Recorded against the wrong location",
            Domain.Common.CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2026-020", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.HighValueSafeId));

        var after = await _harness.History.GetAsync(itemResult.Value);

        Assert.NotNull(after);

        // The original row is still rendered, and marked as corrected.
        var original = after.History.Single(r => r.EventId == locationRow.EventId);
        Assert.True(original.HasCorrections);
        Assert.Contains(nameof(Domain.Events.LocationEvent.StorageLocationPath), original.CorrectedFieldNames);

        // AUD-015 - the projection now reads the corrected value.
        Assert.Equal("High-Value Safe / Drawer 2", after.CurrentLocationPath);

        // And the correction row carries what the page shows beside it.
        var correction = after.History.Single(r => r.Kind == Domain.Common.ItemEventKind.Correction);
        Assert.Equal("MFR-2026-020", correction.CorrectionMfrReference);
        Assert.True(correction.CorrectionSatisfies1_7c3);
    }
}
