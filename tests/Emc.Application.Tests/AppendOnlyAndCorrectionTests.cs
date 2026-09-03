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
    public async Task CorrectingALocationProducesTheCorrectedCurrentLocation()
    {
        // AUD-015 / LOC-001, end to end. The defect this replaces: the correction marked the
        // whole event superseded, projections excluded superseded events, and the item ended up
        // reporting NO current location at all.
        var itemId = await AcceptedItemAsync();

        var locationResult = await _harness.Intake.AssignStorageLocationAsync(
            new AssignLocationRequest(
                itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        Assert.True(locationResult.Succeeded, locationResult.Error);

        var before = await _harness.History.GetAsync(itemId);
        Assert.Equal("Shelf B / Bin 14", before!.CurrentLocationPath);

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        // AUD-016. The request names the replacement LOCATION, not replacement text. The path
        // recorded on the correction is read from that row by the server.
        var correctionResult = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            CorrectedEventId: locationEvent.Id,
            FieldName: nameof(LocationEvent.StorageLocationPath),
            CorrectedValue: null,
            Reason: "Transcription error; the item was placed in Bin 19.",
            Category: CorrectionCategory.PostAcceptanceAccountabilityRecord,
            MfrReference: "MFR-2026-014",
            SupervisorNotifiedUserId: _harness.CommanderUserId,
            SupervisorNotifiedAtUtc: _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.ShelfBBin19Id));

        Assert.True(correctionResult.Succeeded, correctionResult.Error);
        Assert.Empty(correctionResult.Warnings);

        var after = await _harness.History.GetAsync(itemId);

        // THE assertion. Expected Bin 19; the old design produced null.
        Assert.Equal("Shelf B / Bin 19", after!.CurrentLocationPath);

        // AR 195-5 2-5b(5) - the original entry is still there and still readable.
        var original = after.History.Single(r => r.EventId == locationEvent.Id);
        Assert.True(original.HasCorrections);
        Assert.Contains("Bin 14", original.Summary, StringComparison.Ordinal);
        Assert.Equal("Shelf B / Bin 19", original.EffectiveFields[nameof(LocationEvent.StorageLocationPath)]);

        var correction = after.History.Single(r => r.Kind == ItemEventKind.Correction);
        Assert.Equal("Shelf B / Bin 14", correction.CorrectionOriginalValue);
        Assert.Equal("Shelf B / Bin 19", correction.CorrectionNewValue);
        Assert.Equal("MFR-2026-014", correction.CorrectionMfrReference);
        Assert.Equal(_harness.CommanderPrintedNameAndGrade, correction.CorrectionSupervisorNotified);

        // AUD-016. The identifier moved with the text, so anything that resolves the item by
        // location - the monthly 100 percent inventory (AR 195-5 3-1b(2)) among them - now finds
        // it in Bin 19 rather than in the bin the record says was wrong.
        Assert.Equal(CorrectableFieldReference.StorageLocation, correction.CorrectionReferenceKind);
        Assert.Equal(_harness.ShelfBBin14Id, correction.CorrectionOriginalReferenceId);
        Assert.Equal(_harness.ShelfBBin19Id, correction.CorrectionNewReferenceId);
        Assert.Equal(_harness.ShelfBBin19Id, after.CurrentLocationId);

        Assert.True(after.ChainVerification.IsIntact);
    }

    [Fact]
    public async Task TheClientCannotFalsifyTheOriginalValue()
    {
        // AUD-014. RecordCorrectionRequest has no OriginalValue parameter at all - the server
        // derives it from the stored event, so there is nothing to falsify.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        // The corrected text supplied here is deliberately a lie about the replacement bin. It
        // is ignored: for a field that names a row the server reads the text FROM THAT ROW.
        await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), "Anywhere I say",
            "Wrong bin", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-1", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.ShelfBBin19Id));

        var history = await _harness.History.GetAsync(itemId);
        var correction = history!.History.Single(r => r.Kind == ItemEventKind.Correction);

        // The stored original is what the event actually recorded.
        Assert.Equal("Shelf B / Bin 14", correction.CorrectionOriginalValue);

        // And the stored replacement is what the replacement row says, not what was posted.
        Assert.Equal("Shelf B / Bin 19", correction.CorrectionNewValue);
    }

    [Fact]
    public async Task AnUnsupportedFieldNameIsRejected()
    {
        var itemId = await AcceptedItemAsync();

        var anyEvent = await _harness.Db.ItemEvents
            .OfType<StatusEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            anyEvent.Id, "AccountabilityStatus", "Disposed", "attempting to rewrite the workflow",
            CorrectionCategory.PostAcceptanceAccountabilityRecord, null, null, null));

        Assert.False(result.Succeeded);
        Assert.Equal("AUD-014", result.RequirementId);
    }

    [Fact]
    public async Task ALocationCorrectionCannotNameAnotherEvidenceRoomsLocation()
    {
        // AUD-016 with invariant I-08. THE check a reference correction makes necessary. AR 195-5
        // runs the document-number series (2-4c), inspections (3-1) and inventories (3-2) per
        // evidence room; a container in another room is not somewhere this item can be. Assigning
        // a location already enforced this, and a correction must not be a way around a check the
        // original action applied.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), null,
            "Moving it to the other battalion's bin",
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-1", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.OtherRoomLocationId));

        Assert.False(result.Succeeded);
        Assert.Equal("LOC-004", result.RequirementId);

        // Nothing was recorded, so the item is still where it was.
        var history = await _harness.History.GetAsync(itemId);
        Assert.Equal(_harness.ShelfBBin14Id, history!.CurrentLocationId);
        Assert.DoesNotContain(history.History, r => r.Kind == ItemEventKind.Correction);
    }

    [Fact]
    public async Task ALocationCorrectionCannotNameALocationThatDoesNotExist()
    {
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), null, "Wrong bin",
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            null, null, null,
            CorrectedReferenceId: 999_999));

        Assert.False(result.Succeeded);
        Assert.Equal("LOC-004", result.RequirementId);
    }

    [Fact]
    public async Task ALocationCorrectionMustNameAReplacementLocation()
    {
        // Text alone is refused, and the message says what to do instead rather than leaving the
        // custodian to guess why a plausible correction was rejected.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19",
            "Wrong bin", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            null, null, null));

        Assert.False(result.Succeeded);
        Assert.Equal("AUD-016", result.RequirementId);
        Assert.Contains("Select the replacement", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreeTextCorrectionCannotCarryAnIdentifier()
    {
        // The converse. A reason names nothing, so an identifier attached to it would be recorded
        // and projected as though it meant something.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.Reason), "Moved to high-value storage",
            "Wrong reason", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            null, null, null,
            CorrectedReferenceId: _harness.ShelfBBin19Id));

        Assert.False(result.Succeeded);
        Assert.Equal("AUD-016", result.RequirementId);
    }

    [Fact]
    public async Task ACorrectedLocationIsFoundByItsNewIdentifier()
    {
        // AUD-016, stated the way an inventory would ask it: after correcting the record, does a
        // search of Bin 19 find this item, and does a search of Bin 14 no longer find it?
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), null,
            "Recorded against the wrong bin during intake.",
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2026-030", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.ShelfBBin19Id));

        var item = await _harness.Db.EvidenceItems
            .Include(i => i.Events)
            .FirstAsync(i => i.Id == itemId);

        Assert.Equal(_harness.ShelfBBin19Id, item.CurrentLocationId);
        Assert.NotEqual(_harness.ShelfBBin14Id, item.CurrentLocationId);
        Assert.Equal("Shelf B / Bin 19", item.CurrentLocationPath);
    }

    [Fact]
    public async Task ThreeSequentialCorrectionsRecordTheChainEndToEnd()
    {
        // AUD-017, end to end through the service, with real identifiers and sequence numbers
        // assigned by the recorder. Bin 14 -> Bin 19 -> High-Value Safe -> Bin 14 again.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        RecordCorrectionRequest To(int locationId, string reason) => new(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), null, reason,
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2026-040", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: locationId);

        var r1 = await _harness.History.RecordCorrectionAsync(To(_harness.ShelfBBin19Id, "Wrong bin at intake"));
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var r2 = await _harness.History.RecordCorrectionAsync(To(_harness.HighValueSafeId, "Bin 19 was also wrong"));
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var r3 = await _harness.History.RecordCorrectionAsync(To(_harness.ShelfBBin14Id, "It was in Bin 14 after all"));

        Assert.True(r1.Succeeded, r1.Error);
        Assert.True(r2.Succeeded, r2.Error);
        Assert.True(r3.Succeeded, r3.Error);

        var history = await _harness.History.GetAsync(itemId);
        var corrections = history!.History
            .Where(r => r.Kind == ItemEventKind.Correction)
            .OrderBy(r => r.SequenceNumber)
            .ToList();

        Assert.Equal(3, corrections.Count);

        // Each states what IT changed; all keep the original.
        Assert.Equal("Shelf B / Bin 14", corrections[0].CorrectionPreviousValue);
        Assert.Equal("Shelf B / Bin 19", corrections[0].CorrectionNewValue);
        Assert.True(corrections[0].CorrectionCorrectsTheOriginalEntry);

        Assert.Equal("Shelf B / Bin 19", corrections[1].CorrectionPreviousValue);
        Assert.Equal("High-Value Safe / Drawer 2", corrections[1].CorrectionNewValue);
        Assert.False(corrections[1].CorrectionCorrectsTheOriginalEntry);

        Assert.Equal("High-Value Safe / Drawer 2", corrections[2].CorrectionPreviousValue);
        Assert.Equal("Shelf B / Bin 14", corrections[2].CorrectionNewValue);
        Assert.False(corrections[2].CorrectionCorrectsTheOriginalEntry);

        Assert.All(corrections, c => Assert.Equal("Shelf B / Bin 14", c.CorrectionOriginalValue));

        // The record ends where the third correction put it, by identifier and by text.
        Assert.Equal(_harness.ShelfBBin14Id, history.CurrentLocationId);
        Assert.Equal("Shelf B / Bin 14", history.CurrentLocationPath);
        Assert.True(history.ChainVerification.IsIntact);

        // And the audit trail's previous value is what each correction actually changed.
        var audits = _harness.Db.AuditEvents
            .Where(a => a.AffectedRecordType == nameof(CorrectionEvent))
            .OrderBy(a => a.Id)
            .Select(a => a.PreviousValue)
            .ToList();

        Assert.Equal(["Shelf B / Bin 14", "Shelf B / Bin 19", "High-Value Safe / Drawer 2"], audits);
    }

    [Fact]
    public async Task RestatingTheCurrentValueIsRefusedAfterAnEarlierCorrection()
    {
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, null, null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        RecordCorrectionRequest To(int locationId) => new(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), null, "reason",
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-1", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: locationId);

        Assert.True((await _harness.History.RecordCorrectionAsync(To(_harness.ShelfBBin19Id))).Succeeded);

        var again = await _harness.History.RecordCorrectionAsync(To(_harness.ShelfBBin19Id));

        Assert.False(again.Succeeded);
        Assert.Equal("AUD-004", again.RequirementId);
    }

    [Fact]
    public async Task APostAcceptanceCorrectionWithoutAnMfrIsRefusedAndNothingIsRecorded()
    {
        // AUD-005, enforced end to end. The earlier behaviour recorded it and returned a warning.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.Reason), "Moved to high-value storage",
            "Wrong reason recorded", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            MfrReference: null, SupervisorNotifiedUserId: _harness.CommanderUserId,
            SupervisorNotifiedAtUtc: _harness.Clock.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Equal("AUD-005", result.RequirementId);
        Assert.Contains("1-7c(3)", result.Error!, StringComparison.Ordinal);

        var history = await _harness.History.GetAsync(itemId);
        Assert.DoesNotContain(history!.History, r => r.Kind == ItemEventKind.Correction);
        Assert.Equal("Initial placement", history.History.Single(r => r.EventId == locationEvent.Id)
            .EffectiveFields[nameof(LocationEvent.Reason)]);
    }

    [Fact]
    public async Task ASupervisorWithoutAnEmcAccountCanBeRecorded()
    {
        // AUD-018. The responsible CI supervisor (1-7c(3)) is named as the MFR names them.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.Reason), "Moved to high-value storage",
            "Wrong reason recorded", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2026-050", SupervisorNotifiedUserId: null, SupervisorNotifiedAtUtc: null,
            SupervisorNotifiedName: "OKAFOR, ADAEZE N.",
            SupervisorNotifiedGradeOrTitle: "MAJ",
            SupervisorNotifiedOrganization: "902d MI Group S3"));

        Assert.True(result.Succeeded, result.Error);

        var stored = await _harness.Db.ItemEvents.OfType<CorrectionEvent>()
            .SingleAsync(c => c.CorrectsEventId == locationEvent.Id);

        Assert.Null(stored.SupervisorNotifiedUserId);
        Assert.Equal("OKAFOR, ADAEZE N.", stored.SupervisorNotifiedName);
        Assert.Equal("MAJ", stored.SupervisorNotifiedGradeOrTitle);
        Assert.Equal("902d MI Group S3", stored.SupervisorNotifiedOrganization);

        // No time was supplied, so the notification is recorded as contemporaneous.
        Assert.Equal(_harness.Clock.UtcNow, stored.SupervisorNotifiedAtUtc);

        var history = await _harness.History.GetAsync(itemId);
        var row = history!.History.Single(r => r.Kind == ItemEventKind.Correction);
        Assert.Equal("MAJ OKAFOR, ADAEZE N.", row.CorrectionSupervisorNotified);
    }

    [Fact]
    public async Task ASupervisorWithAnAccountIsRecordedFromTheUserRecord()
    {
        // A name posted alongside a user id is not trusted over the user record.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.Reason), "Moved to high-value storage",
            "Wrong reason recorded", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2026-051", _harness.CommanderUserId, _harness.Clock.UtcNow,
            SupervisorNotifiedName: "SOMEBODY ELSE"));

        Assert.True(result.Succeeded, result.Error);

        var stored = await _harness.Db.ItemEvents.OfType<CorrectionEvent>()
            .SingleAsync(c => c.CorrectsEventId == locationEvent.Id);

        Assert.Equal(_harness.CommanderUserId, stored.SupervisorNotifiedUserId);
        Assert.NotEqual("SOMEBODY ELSE", stored.SupervisorNotifiedName);
        Assert.Equal(_harness.CommanderPrintedNameAndGrade, stored.SupervisorNotification!.PrintedNameAndGrade);
    }

    [Fact]
    public async Task AnInactiveOrUnknownSupervisorUserIsRefused()
    {
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var result = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.Reason), "Moved to high-value storage",
            "Wrong reason recorded", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2026-052", 999_999, _harness.Clock.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Equal("AUD-018", result.RequirementId);
    }

    [Fact]
    public async Task SeveralFieldsOfOneEventCanBeCorrectedIndependently()
    {
        // AUD-015. The old "one correction per event, ever" rule made a second error
        // uncorrectable.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement", null));

        var locationEvent = await _harness.Db.ItemEvents
            .OfType<LocationEvent>()
            .FirstAsync(e => e.EvidenceItemId == itemId);

        var first = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.StorageLocationPath), null,
            "Wrong bin", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-1", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.ShelfBBin19Id));

        var second = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            locationEvent.Id, nameof(LocationEvent.Reason), "Moved to high-value storage",
            "Wrong reason recorded", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-2", _harness.CommanderUserId, _harness.Clock.UtcNow));

        Assert.True(first.Succeeded, first.Error);
        Assert.True(second.Succeeded, second.Error);

        var history = await _harness.History.GetAsync(itemId);
        var row = history!.History.Single(r => r.EventId == locationEvent.Id);

        Assert.Equal("Shelf B / Bin 19", row.EffectiveFields[nameof(LocationEvent.StorageLocationPath)]);
        Assert.Equal("Moved to high-value storage", row.EffectiveFields[nameof(LocationEvent.Reason)]);
        Assert.Equal(2, row.CorrectedFieldNames.Count);
        Assert.True(history.ChainVerification.IsIntact);
    }

    [Fact]
    public async Task ACorrectionsOnlyAffectTheEventTheyName()
    {
        // Covered here rather than in the domain suite because unpersisted events all share
        // Id 0 and cannot be distinguished in memory.
        var itemId = await AcceptedItemAsync();

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "First", null));

        _harness.Clock.Advance(TimeSpan.FromHours(1));

        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemId, _harness.HighValueSafeId, _harness.Clock.UtcNow, "Second", null));

        var events = await _harness.Db.ItemEvents.OfType<LocationEvent>()
            .Where(e => e.EvidenceItemId == itemId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();

        // Correct the FIRST location event only.
        await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            events[0].Id, nameof(LocationEvent.StorageLocationPath), null,
            "Wrong initial location", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            "MFR-1", _harness.CommanderUserId, _harness.Clock.UtcNow,
            CorrectedReferenceId: _harness.ShelfBBin19Id));

        var history = await _harness.History.GetAsync(itemId);

        // The second event is untouched, and it is still the current location.
        var secondRow = history!.History.Single(r => r.EventId == events[1].Id);
        Assert.False(secondRow.HasCorrections);
        Assert.Equal("High-Value Safe / Drawer 2", history.CurrentLocationPath);

        var firstRow = history.History.Single(r => r.EventId == events[0].Id);
        Assert.True(firstRow.HasCorrections);
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
