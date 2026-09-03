using Emc.Domain.Common;
using Emc.Domain.Events;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// Effective-value projection over corrected events.
///
/// These are the regressions for the most serious defect in the previous design: correcting a
/// field marked the WHOLE event superseded, and current-state projections excluded superseded
/// events — so correcting an item's location from Bin 14 to Bin 19 left the item reporting NO
/// location at all.
///
/// Requirements: AUD-015, LOC-001, COC-001.
/// </summary>
public class EffectiveProjectionTests
{
    private static readonly DateTimeOffset Local =
        new(2026, 9, 3, 9, 15, 0, TimeSpan.FromHours(-4));

    private static LocationEvent NewLocationEvent(int sequence, string path)
    {
        var locationEvent = new LocationEvent(
            storageLocationId: 1,
            storageLocationPath: path,
            occurredAtLocal: Local.AddMinutes(sequence),
            recordedAtUtc: Local.ToUniversalTime().AddMinutes(sequence),
            recordedByUserId: 1,
            reason: "Initial placement",
            notes: "Original note");

        locationEvent.AssignSequence(100, sequence);
        return locationEvent;
    }

    private static CorrectionEvent Correct(
        ItemEvent target, string field, string? value, int sequence, string reason = "Recorded in error")
    {
        var correction = CorrectionFactory.Create(
            target, field, value, reason,
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            Local.AddMinutes(sequence), Local.ToUniversalTime().AddMinutes(sequence), 17,
            mfrReference: "MFR-2026-014", supervisorNotifiedUserId: 4,
            supervisorNotifiedAtUtc: Local.ToUniversalTime().AddMinutes(sequence));

        correction.AssignSequence(100, sequence);
        return correction;
    }

    [Fact]
    public void CorrectingALocationProducesTheCorrectedCurrentLocation()
    {
        // The headline regression. Expected: Bin 19. Previously: null.
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var correction = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 2);

        var current = EffectiveHistory.LatestOf<LocationEvent>([location, correction]);

        Assert.NotNull(current);
        Assert.Equal(
            "Shelf B / Bin 19",
            current.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
    }

    [Fact]
    public void CorrectingALocationDoesNotFallBackToAnEarlierLocation()
    {
        // The other failure mode the old design could produce: excluding the corrected event made
        // the projection silently report a stale, earlier location.
        var first = NewLocationEvent(1, "Intake");
        var second = NewLocationEvent(2, "Shelf B / Bin 14");
        var correction = Correct(second, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 3);

        var current = EffectiveHistory.LatestOf<LocationEvent>([first, second, correction]);

        Assert.Equal(
            "Shelf B / Bin 19",
            current!.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));

        Assert.NotEqual(
            "Intake",
            current.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
    }

    [Fact]
    public void CorrectingOneFieldLeavesTheOthersAtTheirOriginalValues()
    {
        // AUD-015. Field-level, not event-level: correcting the path must not disturb the reason
        // or the notes recorded alongside it.
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var correction = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 2);

        var effective = new EffectiveItemEvent(location, [correction]);

        Assert.Equal("Shelf B / Bin 19", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
        Assert.Equal("Initial placement", effective.EffectiveValueOf(nameof(LocationEvent.Reason)));
        Assert.Equal("Original note", effective.EffectiveValueOf(nameof(LocationEvent.Notes)));

        Assert.Single(effective.CorrectedFieldNames);
        Assert.True(effective.IsCorrected(nameof(LocationEvent.StorageLocationPath)));
        Assert.False(effective.IsCorrected(nameof(LocationEvent.Reason)));
    }

    [Fact]
    public void SeveralIndependentFieldsOfOneEventCanBeCorrected()
    {
        // The old "one correction per event, ever" rule made a second error uncorrectable.
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var pathFix = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 2);
        var reasonFix = Correct(location, nameof(LocationEvent.Reason), "Moved to high-value storage", 3);

        var effective = new EffectiveItemEvent(location, [pathFix, reasonFix]);

        Assert.Equal("Shelf B / Bin 19", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
        Assert.Equal("Moved to high-value storage", effective.EffectiveValueOf(nameof(LocationEvent.Reason)));
        Assert.Equal(2, effective.CorrectedFieldNames.Count);
    }

    [Fact]
    public void AFieldCorrectedTwiceTakesTheMostRecentCorrection()
    {
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var first = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 2);
        var second = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 21", 3);

        var effective = new EffectiveItemEvent(location, [first, second]);

        Assert.Equal("Shelf B / Bin 21", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));

        // Both corrections remain visible — AR 195-5 2-5b(5) keeps every entry readable.
        Assert.Equal(2, effective.Corrections.Count);
        Assert.Equal("Shelf B / Bin 14", effective.Corrections[0].OriginalValue);
    }

    [Fact]
    public void TheOriginalEventRemainsAvailableAfterCorrection()
    {
        // AR 195-5 2-5b(5) — the struck-through entry must still be readable.
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var correction = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 2);

        var effective = new EffectiveItemEvent(location, [correction]);

        Assert.Same(location, effective.Original);
        Assert.Equal("Shelf B / Bin 14", location.StorageLocationPath);
        Assert.Equal("Shelf B / Bin 14", effective.Corrections[0].OriginalValue);
    }

    [Fact]
    public void CorrectingACustodyRecipientProducesTheCorrectedCurrentCustody()
    {
        // COC-001. Correcting who received an item changes who holds it; it does not erase the
        // transfer.
        var custody = new CustodyEvent(
            releasedBy: CustodyParty.ForOrganization("902d MI Group"),
            receivedBy: CustodyParty.ForExternalPerson("SMITH, JOHN A.", "SA", "902d MI Group", true),
            purposeOfChangeOfCustody: "Received into evidence room",
            occurredAtLocal: Local,
            recordedAtUtc: Local.ToUniversalTime(),
            recordedByUserId: 1,
            isScrcni: false);

        custody.AssignSequence(100, 1);

        var correction = Correct(custody, nameof(CustodyEvent.ReceivedBy), "JONES, MARY B.", 2);
        var current = EffectiveHistory.LatestOf<CustodyEvent>([custody, correction]);

        Assert.Equal("JONES, MARY B.", current!.EffectiveValueOf(nameof(CustodyEvent.ReceivedBy)));

        // The releasing party was not corrected and keeps its original value.
        Assert.Equal("902d MI Group", current.EffectiveValueOf(nameof(CustodyEvent.ReleasedBy)));
    }

    [Fact]
    public void ProjectExcludesCorrectionsAsEventsInTheirOwnRight()
    {
        // A correction is not a custody transfer or a location change. It appears in the raw
        // history for display, but never as an event of the type it corrects.
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var correction = Correct(location, nameof(LocationEvent.StorageLocationPath), "Shelf B / Bin 19", 2);

        var projected = EffectiveHistory.Project([location, correction]);

        Assert.Single(projected);
        Assert.Same(location, projected[0].Original);
        Assert.True(projected[0].HasCorrections);
    }

    [Fact]
    public void AnUncorrectedEventProjectsItsOriginalValues()
    {
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var effective = new EffectiveItemEvent(location, []);

        Assert.False(effective.HasCorrections);
        Assert.Empty(effective.CorrectedFieldNames);
        Assert.Equal("Shelf B / Bin 14", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
    }

    // Note: "corrections belonging to another event are ignored" cannot be asserted here.
    // Unpersisted events all carry Id 0, so CorrectsEventId cannot distinguish them in memory.
    // That behaviour is covered in Emc.Application.Tests, where identifiers are real.
}
