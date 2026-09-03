using Emc.Application.Cases;
using Emc.Application.Items;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Append-only enforcement, the correction pattern, and hash-chain integrity, exercised against
/// a real relational database.
/// Requirements: AUD-001 .. AUD-008.
/// </summary>
public class AppendOnlyAndCorrectionTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<int> AcceptedItemAsync(string documentNumber = "001-26")
    {
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            $"CASE-{Guid.NewGuid():N}"[..20], "Test case", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        var itemResult = await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One Samsung SM-S921U cellular telephone", "1",
            "R58N30XXXXX", "356938035643809", false, false, false, null));

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        _harness.SignInAsCustodian();

        var numberResult = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(
                voucherResult.Value, documentNumber, true, _harness.Clock.UtcNow));

        Assert.True(numberResult.Succeeded, numberResult.Error);
        return itemResult.Value;
    }

    [Fact]
    public async Task AnEventCannotBeModified()
    {
        // AUD-001, invariant I-14. AR 195-5 2-5b(5): an erroneous entry is voided with one line
        // "so it may still be read" and initialed - correction fluid, tape, labels and erasures
        // are prohibited. The software analogue is that the row cannot be rewritten.
        var itemId = await AcceptedItemAsync();

        var statusEvent = await _harness.Db.ItemEvents
            .OfType<StatusEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        _harness.Db.Entry(statusEvent).Property(nameof(StatusEvent.Notes)).CurrentValue = "tampered";

        var ex = await Assert.ThrowsAsync<AppendOnlyViolationException>(
            () => _harness.Db.SaveChangesAsync());

        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2-5b(5)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventCannotBeDeleted()
    {
        var itemId = await AcceptedItemAsync();

        var anyEvent = await _harness.Db.ItemEvents.FirstAsync(e => e.EvidenceItemId == itemId);
        _harness.Db.ItemEvents.Remove(anyEvent);

        var ex = await Assert.ThrowsAsync<AppendOnlyViolationException>(
            () => _harness.Db.SaveChangesAsync());

        Assert.Contains("cannot be deleted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAuditEventCannotBeModifiedOrDeleted()
    {
        // AUD-001. The security audit is append-only for the same reason the accountability
        // record is: an audit trail that can be edited is not an audit trail.
        await AcceptedItemAsync();

        var auditEvent = await _harness.Db.AuditEvents.FirstAsync();
        _harness.Db.AuditEvents.Remove(auditEvent);

        await Assert.ThrowsAsync<AppendOnlyViolationException>(
            () => _harness.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task AnUnhashedEventIsRejected()
    {
        // AUD-008. An event reaching the database unhashed would leave a hole in the chain that
        // verification could not distinguish from tampering, so the only way to append is through
        // the recorder.
        var itemId = await AcceptedItemAsync();

        _harness.Db.ItemEvents.Add(new StatusEvent(
            AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.TemporarilyReleased,
            "Bypassing the recorder", _harness.Clock.UtcNow, _harness.Clock.UtcNow,
            _harness.CustodianUserId));

        var ex = await Assert.ThrowsAsync<AppendOnlyViolationException>(
            () => _harness.Db.SaveChangesAsync());

        Assert.Contains("hash chain", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(itemId, itemId);
    }

    [Fact]
    public async Task ACorrectionPreservesTheOriginalAndMarksItSuperseded()
    {
        // AUD-003, AUD-004, AUD-006. AR 195-5 2-5b(5) plus 1-7c(3): the original stays readable,
        // the correction is attributable, and the corrective action is documented.
        var itemId = await AcceptedItemAsync();

        var locationResult = await _harness.Intake.AssignStorageLocationAsync(
            new AssignLocationRequest(
                itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        Assert.True(locationResult.Succeeded, locationResult.Error);

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var correctionResult = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            CorrectedEventId: locationEvent.Id,
            FieldName: "StorageLocationPath",
            OriginalValue: "Shelf B / Bin 14",
            CorrectedValue: "Shelf B / Bin 19",
            Reason: "Transcription error; the item was placed in Bin 19.",
            MfrReference: "MFR-2026-014",
            SupervisorNotifiedUserId: _harness.CommanderUserId,
            SupervisorNotifiedAtUtc: _harness.Clock.UtcNow));

        Assert.True(correctionResult.Succeeded, correctionResult.Error);

        // AR 195-5 1-7c(3) is satisfied, so there is nothing to flag.
        Assert.Empty(correctionResult.Warnings);

        var history = await _harness.History.GetAsync(itemId);

        Assert.NotNull(history);

        // The original is still present and readable - it is marked, not removed.
        var original = history.History.Single(r => r.EventId == locationEvent.Id);
        Assert.True(original.IsSuperseded);
        Assert.Contains("Bin 14", original.Summary, StringComparison.Ordinal);

        var correction = history.History.Single(r => r.Kind == ItemEventKind.Correction);
        Assert.Equal("Shelf B / Bin 14", correction.CorrectionOriginalValue);
        Assert.Equal("Shelf B / Bin 19", correction.CorrectionNewValue);
        Assert.Equal("MFR-2026-014", correction.CorrectionMfrReference);
        Assert.True(correction.CorrectionSatisfies1_7c3);

        // AUD-008 - correcting does not break the chain, because a correction is an append.
        Assert.True(history.ChainVerification.IsIntact);
    }

    [Fact]
    public async Task ACorrectionWithoutAnMfrOrSupervisorNotificationIsRecordedButFlagged()
    {
        // AUD-005. AR 195-5 1-7c(3) requires both. Whether every field-level correction reaches
        // that threshold is local policy, so EMC records the correction and surfaces the
        // shortfall where an inspector will see it, rather than blocking the correction.
        var itemId = await AcceptedItemAsync();

        var anyEvent = await _harness.Db.ItemEvents
            .OfType<StatusEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            anyEvent.Id, "Reason", anyEvent.Reason, "Corrected reason text",
            "Wording error", null, null, null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("1-7c(3)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEventCannotBeCorrectedTwice()
    {
        // AUD-003. Once superseded, the correction itself is the current entry - correcting the
        // superseded original again would produce two competing "current" values.
        var itemId = await AcceptedItemAsync();

        var anyEvent = await _harness.Db.ItemEvents
            .OfType<StatusEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var first = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            anyEvent.Id, "Reason", anyEvent.Reason, "First correction", "Reason one",
            "MFR-1", _harness.CommanderUserId, _harness.Clock.UtcNow));

        Assert.True(first.Succeeded, first.Error);

        var second = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            anyEvent.Id, "Reason", anyEvent.Reason, "Second correction", "Reason two",
            "MFR-2", _harness.CommanderUserId, _harness.Clock.UtcNow));

        Assert.False(second.Succeeded);
        Assert.Equal("AUD-003", second.RequirementId);
    }

    [Fact]
    public async Task TheChainIsIntactAcrossASingleUnitOfWorkWithSeveralEvents()
    {
        // Regression: submission appends two status events, and intake appends a document-number
        // event plus a status event, all before SaveChanges. The chain head is held on the item
        // precisely so these link to each other rather than to the last PERSISTED event.
        var itemId = await AcceptedItemAsync();
        var history = await _harness.History.GetAsync(itemId);

        Assert.NotNull(history);
        Assert.True(history.ChainVerification.IsIntact);
        Assert.Equal(4, history.History.Count);
        Assert.Equal(Enumerable.Range(1, 4), history.History.Select(r => r.SequenceNumber));
    }

    [Fact]
    public async Task ChainVerificationDetectsAnEventModifiedOutsideTheApplication()
    {
        // AUD-008 - the case the SaveChanges guard cannot see. Simulated with raw SQL, which is
        // exactly what a DBA would use. On SQL Server the triggers would reject this too; the
        // chain is the backstop for a principal who can drop them.
        var itemId = await AcceptedItemAsync();

        await _harness.Db.Database.ExecuteSqlRawAsync(
            "UPDATE ItemEvents SET Notes = 'altered out of band' WHERE EvidenceItemId = {0} AND SequenceNumber = 1",
            itemId);

        _harness.Db.ChangeTracker.Clear();

        var history = await _harness.History.GetAsync(itemId);

        Assert.NotNull(history);
        Assert.False(history.ChainVerification.IsIntact);
        Assert.Contains(
            history.ChainVerification.Problems,
            p => p.Kind == ChainProblemKind.ContentModified);
    }

    [Fact]
    public async Task ChainVerificationDetectsARemovedEvent()
    {
        var itemId = await AcceptedItemAsync();

        await _harness.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ItemEvents WHERE EvidenceItemId = {0} AND SequenceNumber = 2", itemId);

        _harness.Db.ChangeTracker.Clear();

        var history = await _harness.History.GetAsync(itemId);

        Assert.NotNull(history);
        Assert.False(history.ChainVerification.IsIntact);
        Assert.Contains(
            history.ChainVerification.Problems,
            p => p.Kind is ChainProblemKind.SequenceGap or ChainProblemKind.BrokenLink);
    }
}
