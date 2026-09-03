using Emc.Application.Cases;
using Emc.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The first vertical slice end to end: case -> voucher -> items -> submission -> custodian
/// intake -> location -> item history.
/// </summary>
public class VerticalSliceTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private int _caseSequence;

    private async Task<(int CaseId, int VoucherId, int ItemId)> CreateSubmittedVoucherAsync()
    {
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            $"{++_caseSequence:D4}-2026-CID902-XXXXX", "Unauthorized disclosure investigation", null,
            _harness.EvidenceRoomId));

        Assert.True(caseResult.Succeeded, caseResult.Error);

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            CaseId: caseResult.Value,
            ReceivingActivity: "902d MI Group Evidence Room",
            ReceivingActivityLocation: "Fort Meade, MD",
            ReceivedFrom: "SUBJECT residence, 123 Elm Street",
            AcquiredAtLocal: new DateTimeOffset(2026, 9, 3, 9, 15, 0, TimeSpan.FromHours(-4)),
            IsRequestForAssistance: false,
            RequestingOfficeCaseNumber: null));

        Assert.True(voucherResult.Succeeded, voucherResult.Error);

        var itemResult = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            VoucherId: voucherResult.Value,
            Description: "One Samsung SM-S921U cellular telephone, black, in a clear plastic case",
            Quantity: "1",
            SerialNumber: "R58N30XXXXX",
            UniqueDeviceIdentifier: "356938035643809",
            IsPossibleBiohazard: false,
            IsFungible: false,
            IsSealed: false,
            SealDescription: null));

        Assert.True(itemResult.Succeeded, itemResult.Error);

        var submitResult = await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);
        Assert.True(submitResult.Succeeded, submitResult.Error);

        return (caseResult.Value, voucherResult.Value, itemResult.Value);
    }

    [Fact]
    public async Task TheFullSlice_ProducesACompleteChronologicalItemHistory()
    {
        var (_, voucherId, itemId) = await CreateSubmittedVoucherAsync();

        _harness.SignInAsCustodian();

        // AR 195-5 2-4c - the custodian transcribes the number assigned in the bound ledger.
        var numberResult = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(
                VoucherId: voucherId,
                DocumentNumber: "037-26",
                AttestedAssignedInAuthoritativeLedger: true,
                ReceivedAtLocal: new DateTimeOffset(2026, 9, 3, 9, 31, 0, TimeSpan.FromHours(-4))));

        Assert.True(numberResult.Succeeded, numberResult.Error);

        // AR 195-5 2-4e - the evidence-room location.
        var locationResult = await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            ItemId: itemId,
            StorageLocationId: _harness.ShelfBBin14Id,
            OccurredAtLocal: new DateTimeOffset(2026, 9, 3, 9, 31, 0, TimeSpan.FromHours(-4)),
            Reason: "Initial placement following intake",
            Notes: null));

        Assert.True(locationResult.Succeeded, locationResult.Error);

        // LOC-002 - a second location. AR 195-5 2-4e would have the first entry ERASED on paper;
        // EMC retains the history as a design and integrity control.
        _harness.Clock.Advance(TimeSpan.FromDays(46));

        var moveResult = await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            ItemId: itemId,
            StorageLocationId: _harness.HighValueSafeId,
            OccurredAtLocal: new DateTimeOffset(2026, 10, 19, 13, 22, 0, TimeSpan.FromHours(-4)),
            Reason: "Moved to high-value storage",
            Notes: null));

        Assert.True(moveResult.Succeeded, moveResult.Error);

        var history = await _harness.History.GetAsync(itemId);

        Assert.NotNull(history);
        Assert.Equal("037-26", history.VoucherIdentifier);
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, history.AccountabilityStatus);
        // The path walks the storage-location hierarchy within the evidence room; the room
        // itself is not a storage location and is shown alongside, not inside, the path.
        Assert.Equal("High-Value Safe / Drawer 2", history.CurrentLocationPath);

        // Draft -> Acquired, Acquired -> AwaitingCustodian, document number,
        // AwaitingCustodian -> InEvidenceRoom, location, location.
        Assert.Equal(6, history.History.Count);

        // The history is a single ordered sequence across all event kinds.
        Assert.Equal(
            Enumerable.Range(1, 6),
            history.History.Select(r => r.SequenceNumber));

        // AUD-008 - the chain verifies on every view.
        Assert.True(history.ChainVerification.IsIntact);

        // LOC-002 - the earlier location is still there. AR 195-5 2-4e does not require this;
        // EMC keeps it deliberately.
        Assert.Contains(history.History, r => r.Summary.Contains("Bin 14", StringComparison.Ordinal));
        Assert.Contains(history.History, r => r.Summary.Contains("High-Value Safe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADocumentNumberCannotBeReusedInTheSameRoomAndYear()
    {
        // Invariant I-04. AR 195-5 2-4c numbers documents in sequence within the calendar year;
        // two vouchers cannot share a number in one evidence room.
        var first = await CreateSubmittedVoucherAsync();
        var second = await CreateSubmittedVoucherAsync();

        _harness.SignInAsCustodian();

        var ok = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(first.VoucherId, "037-26", true, _harness.Clock.UtcNow));

        Assert.True(ok.Succeeded, ok.Error);

        var duplicate = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(second.VoucherId, "037-26", true, _harness.Clock.UtcNow));

        Assert.False(duplicate.Succeeded);
        Assert.Equal("VCH-005", duplicate.RequirementId);
    }

    [Fact]
    public async Task ASequenceGapProducesAWarningAndNotABlock()
    {
        // VCH-009. EMC cannot know the ledger's true state - AR 195-5 2-4c assigns numbers by
        // order of precedence FROM THE LEDGER - so a gap means a voucher probably has not been
        // entered here yet, not that the ledger is wrong. Warn, never block.
        var (_, voucherId, _) = await CreateSubmittedVoucherAsync();

        _harness.SignInAsCustodian();

        var result = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherId, "037-26", true, _harness.Clock.UtcNow));

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("036-26", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("advisory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordingADocumentNumberWithoutTheLedgerAttestationIsRefused()
    {
        // EMC-002. This is the constraint that defines V1: AR 195-5 2-4c makes assignment the
        // custodian's act from the ledger, and 2-5c requires Army G-2X approval before a CI
        // organization may use a stand-alone automated accountability system.
        var (_, voucherId, _) = await CreateSubmittedVoucherAsync();

        _harness.SignInAsCustodian();

        var result = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(
                voucherId, "037-26",
                AttestedAssignedInAuthoritativeLedger: false,
                ReceivedAtLocal: _harness.Clock.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Equal("EMC-002", result.RequirementId);
        Assert.Contains("2-4c", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedDocumentNumberIsRefusedWithTheRegulatoryCitation()
    {
        var (_, voucherId, _) = await CreateSubmittedVoucherAsync();

        _harness.SignInAsCustodian();

        var result = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherId, "37-2026", true, _harness.Clock.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-004", result.RequirementId);
        Assert.Contains("2-4c", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALocationCannotBeAssignedBeforeTheEvidenceIsReceived()
    {
        // Invariant I-12. AR 195-5 2-4e puts the location in the DA Form 4137's location block,
        // which presupposes receipt into the evidence room under 2-4c.
        var (_, _, itemId) = await CreateSubmittedVoucherAsync();

        _harness.SignInAsCustodian();

        var result = await _harness.Intake.AssignStorageLocationAsync(
            new AssignLocationRequest(itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        Assert.False(result.Succeeded);
        Assert.Contains("2-4c", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVoucherCannotBeSubmittedWithNoItems()
    {
        // VCH-011. AR 195-5 2-3a.
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0143-2026-CID902-XXXXX", "Empty voucher test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var result = await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-011", result.RequirementId);
    }

    [Fact]
    public async Task ItemsCannotBeAddedOnceTheVoucherHasBeenSubmitted()
    {
        // VCH-010, invariant I-10. AR 195-5 2-3g makes the draft the place for corrections;
        // afterwards, a correction that leaves the original readable (2-5b(5)).
        var (_, voucherId, _) = await CreateSubmittedVoucherAsync();

        _harness.SignInAsAgent();

        var result = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherId, "A late addition", "1", null, null, false, false, false, null));

        Assert.False(result.Succeeded);
        Assert.Equal("VCH-010", result.RequirementId);
    }

    [Fact]
    public async Task ASuppositionPhraseProducesAWarningAndNotABlock()
    {
        // ITEM-003. AR 195-5 2-3d prohibits supposition, and gives "suspected to be marijuana" as
        // its own example - but a keyword list cannot reliably tell a prohibited inference from a
        // legitimate description, so EMC warns.
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0144-2026-CID902-XXXXX", "Supposition test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var result = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "A green leafy substance suspected to be marijuana",
            "approximately 14 grams", null, null, false, true, false, null));

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("2-3d", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARequestForAssistanceRecordsBothCaseNumbers()
    {
        // AR 195-5 2-3b: for evidence collected in response to an RFA, "both the seizing and
        // requesting offices law enforcement report number will be recorded" (CASE-002).
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0145-2026-CID902-XXXXX", "RFA test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow,
            IsRequestForAssistance: true,
            RequestingOfficeCaseNumber: "0091-2026-CID310-YYYYY"));

        Assert.True(voucherResult.Succeeded, voucherResult.Error);

        var voucher = await _harness.Db.EvidenceVouchers.FindAsync(voucherResult.Value);

        Assert.True(voucher!.IsRequestForAssistance);
        Assert.Equal("0091-2026-CID310-YYYYY", voucher.RequestingOfficeCaseNumber);
    }

    [Fact]
    public async Task ADuplicateCaseControlNumberIsRefused()
    {
        _harness.SignInAsAgent();

        var request = new CreateCaseRequest(
            "0146-2026-CID902-XXXXX", "Duplicate test", null, _harness.EvidenceRoomId);

        Assert.True((await _harness.Cases.CreateAsync(request)).Succeeded);

        var duplicate = await _harness.Cases.CreateAsync(request);

        Assert.False(duplicate.Succeeded);
        Assert.Equal("CASE-001", duplicate.RequirementId);
    }
}
