using Emc.Application.Cases;
using Emc.Application.Integrity;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Out-of-band changes to an item's stored summary are caught, and reported apart from chain
/// failures. SQLite has no append-only triggers (SQL Server only), so a raw UPDATE goes through
/// here - which is exactly the out-of-band change the verifier exists to catch.
/// Requirements: AUD-008, AUD-021, IAM-009.
/// </summary>
public class IntegrityVerificationTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private IntegrityVerificationService Service()
        => new(_harness.Db, _harness.Authorization, _harness.Audit, _harness.Clock);

    private int _nextSequence;

    private async Task<int> AcceptedItemAsync()
    {
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            $"CASE-{Guid.NewGuid():N}"[..20], "Integrity test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var itemResult = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One Samsung SM-S921U cellular telephone", "1",
            "R58N30XXXXX", "356938035643809", false, false, false, null));

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        _harness.SignInAsCustodian();
        var numberResult = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherResult.Value, $"{++_nextSequence:D3}-26", true, _harness.Clock.UtcNow));
        Assert.True(numberResult.Succeeded, numberResult.Error);

        return itemResult.Value;
    }

    [Fact]
    public async Task AnUntouchedItemVerifiesOnBothChecks()
    {
        var itemId = await AcceptedItemAsync();

        var view = await _harness.History.GetAsync(itemId);

        Assert.True(view!.ChainVerification.IsIntact);
        Assert.True(view.SnapshotVerification!.IsConsistent);
    }

    [Fact]
    public async Task ARawStatusChangeIsASnapshotMismatch_WhileTheChainStillVerifies()
    {
        // AUD-021. THE case from the review: UPDATE EvidenceItems SET AccountabilityStatus only.
        // The events are untouched, so the chain passes; the item now claims to be Disposed
        // while its history ends in InEvidenceRoom.
        var itemId = await AcceptedItemAsync();

        await _harness.Db.Database.ExecuteSqlRawAsync(
            "UPDATE EvidenceItems SET AccountabilityStatus = {0} WHERE Id = {1}",
            (int)AccountabilityStatus.Disposed, itemId);

        var view = await _harness.History.GetAsync(itemId);

        Assert.True(view!.ChainVerification.IsIntact);
        Assert.False(view.SnapshotVerification!.IsConsistent);

        var problem = Assert.Single(view.SnapshotVerification.Problems);
        Assert.Equal(SnapshotProblemKind.StatusMismatch, problem.Kind);
        Assert.Equal("InEvidenceRoom", problem.Expected);
        Assert.Equal("Disposed", problem.Actual);
    }

    [Fact]
    public async Task ARawSequenceOrHeadChangeIsReportedByKind()
    {
        var itemId = await AcceptedItemAsync();

        await _harness.Db.Database.ExecuteSqlRawAsync(
            "UPDATE EvidenceItems SET LastEventSequenceNumber = 99, LastEventHash = 'tampered' WHERE Id = {0}", itemId);

        var view = await _harness.History.GetAsync(itemId);

        Assert.True(view!.ChainVerification.IsIntact);
        Assert.Equal(
            [SnapshotProblemKind.SequenceMismatch, SnapshotProblemKind.HashMismatch],
            view.SnapshotVerification!.Problems.Select(p => p.Kind).ToList());
    }

    [Fact]
    public async Task ARawEventChangeIsAChainFailure_NotASnapshotMismatch()
    {
        // The other direction, to prove the two checks are distinct. Altering an event breaks
        // the chain; the item's summary still matches the (altered) latest event's stored hash
        // and sequence, so the snapshot check has nothing to say.
        var itemId = await AcceptedItemAsync();

        var firstEvent = await _harness.Db.ItemEvents.AsNoTracking()
            .Where(e => e.EvidenceItemId == itemId).OrderBy(e => e.SequenceNumber).FirstAsync();

        await _harness.Db.Database.ExecuteSqlRawAsync(
            "UPDATE ItemEvents SET Notes = 'tampered' WHERE Id = {0}", firstEvent.Id);

        var view = await _harness.History.GetAsync(itemId);

        Assert.False(view!.ChainVerification.IsIntact);
        Assert.True(view.SnapshotVerification!.IsConsistent);
    }

    [Fact]
    public async Task TheRoomReportSeparatesChainFailuresFromSnapshotMismatches()
    {
        var statusTampered = await AcceptedItemAsync();
        var eventTampered = await AcceptedItemAsync();
        var clean = await AcceptedItemAsync();

        await _harness.Db.Database.ExecuteSqlRawAsync(
            "UPDATE EvidenceItems SET AccountabilityStatus = {0} WHERE Id = {1}",
            (int)AccountabilityStatus.Disposed, statusTampered);

        var anEvent = await _harness.Db.ItemEvents.AsNoTracking()
            .Where(e => e.EvidenceItemId == eventTampered).OrderBy(e => e.SequenceNumber).FirstAsync();
        await _harness.Db.Database.ExecuteSqlRawAsync(
            "UPDATE ItemEvents SET Notes = 'tampered' WHERE Id = {0}", anEvent.Id);

        // The application administrator holds VerifyIntegrity and NO evidence-read permission.
        _harness.SignInAsAdministrator();
        var result = await Service().VerifyEvidenceRoomAsync(_harness.EvidenceRoomId);

        Assert.True(result.Succeeded, result.Error);
        var report = result.Value!;

        Assert.Equal(3, report.ItemsChecked);
        Assert.Equal(1, report.EventChainFailures);
        Assert.Equal(1, report.SnapshotMismatches);
        Assert.False(report.IsIntact);

        Assert.Contains(report.Findings, f => f.ItemId == statusTampered && f.Chain.IsIntact && !f.Snapshot.IsConsistent);
        Assert.Contains(report.Findings, f => f.ItemId == eventTampered && !f.Chain.IsIntact);
        Assert.DoesNotContain(report.Findings, f => f.ItemId == clean);

        // The run is on the audit record.
        Assert.Contains(_harness.Db.AuditEvents, a => a.EventType == AuditEventType.IntegrityVerificationRun);
    }

    [Fact]
    public async Task TheReportCarriesNoEvidenceContent()
    {
        // IAM-009 / IAM-017. The administrator may verify integrity but may not read evidence.
        // The report's shape has identifiers and problems only - asserted structurally, so a
        // description field cannot be added to a finding without this test noticing.
        var props = typeof(ItemIntegrityRow).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string> { "ItemId", "VoucherId", "VoucherIdentifier", "ItemNumber", "Chain", "Snapshot", "IsIntact" },
            props);
    }

    [Fact]
    public async Task AnAgentCannotRunTheRoomReport()
    {
        await AcceptedItemAsync();
        _harness.SignInAsAgent();

        var result = await Service().VerifyEvidenceRoomAsync(_harness.EvidenceRoomId);

        Assert.False(result.Succeeded);
    }
}
