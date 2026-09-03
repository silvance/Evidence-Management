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

    /// <summary>
    /// Bin 14 is storage location 14, Bin 19 is 19, and so on, so that a projection asserting an
    /// identifier is asserting something distinguishable rather than a constant.
    /// </summary>
    private static LocationEvent NewLocationEvent(int sequence, string path, int locationId = 14)
    {
        var locationEvent = new LocationEvent(
            storageLocationId: locationId,
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
        ItemEvent target,
        string field,
        string? value,
        int sequence,
        string reason = "Recorded in error",
        IEnumerable<CorrectionEvent>? existing = null)
    {
        var correction = CorrectionFactory.Create(
            target, existing ?? [], field, value, reason,
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            Local.AddMinutes(sequence), Local.ToUniversalTime().AddMinutes(sequence), 17,
            mfrReference: "MFR-2026-014",
            supervisorNotification: SupervisorNotification.OfPerson(
                "RIVERA, LUIS M.", "CW3", "902d MI Group", Local.ToUniversalTime().AddMinutes(sequence)));

        correction.AssignSequence(100, sequence);
        return correction;
    }

    /// <summary>
    /// Corrects a field that names a ROW. The display text stands in for what the application
    /// service reads from the replacement row; it is never taken from a form (AUD-016).
    /// </summary>
    private static CorrectionEvent CorrectTo(
        ItemEvent target,
        string field,
        int toReferenceId,
        string toDisplayText,
        int sequence,
        string reason = "Recorded in error",
        IEnumerable<CorrectionEvent>? existing = null)
    {
        var correction = CorrectionFactory.CreateReferenceCorrection(
            target, existing ?? [], field, toReferenceId, toDisplayText, reason,
            CorrectionCategory.PostAcceptanceAccountabilityRecord,
            Local.AddMinutes(sequence), Local.ToUniversalTime().AddMinutes(sequence), 17,
            mfrReference: "MFR-2026-014",
            supervisorNotification: SupervisorNotification.OfPerson(
                "RIVERA, LUIS M.", "CW3", "902d MI Group", Local.ToUniversalTime().AddMinutes(sequence)));

        correction.AssignSequence(100, sequence);
        return correction;
    }

    [Fact]
    public void CorrectingALocationProducesTheCorrectedCurrentLocation()
    {
        // The headline regression. Expected: Bin 19. Previously: null.
        var location = NewLocationEvent(1, "Shelf B / Bin 14");
        var correction = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

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
        var first = NewLocationEvent(1, "Intake", 3);
        var second = NewLocationEvent(2, "Shelf B / Bin 14", 14);
        var correction = CorrectTo(second, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 3);

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
        var correction = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

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
        var pathFix = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);
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
        var first = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);
        var second = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 21, "Shelf B / Bin 21", 3, existing: [first]);

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
        var correction = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

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

        var correction = CorrectTo(custody, nameof(CustodyEvent.ReceivedBy), 55, "JONES, MARY B.", 2);
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
        var correction = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

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

    [Fact]
    public void CorrectingALocationMovesTheIdentifier_NotOnlyTheDisplayedPath()
    {
        // AUD-016. The half the earlier text-only model lost. Everything that answers "which
        // items are in this container" - the monthly 100 percent inventory (AR 195-5 3-1b(2)),
        // inventory reconstruction (3-2), discrepancy work (3-3a) - resolves the location by
        // identifier. A correction that changed only the displayed path left those pointing at
        // the bin the record had just said was wrong.
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var correction = CorrectTo(
            location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

        var current = EffectiveHistory.LatestOf<LocationEvent>([location, correction]);

        Assert.Equal("Shelf B / Bin 19", current!.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
        Assert.Equal(19, current.EffectiveStorageLocationId);

        // And the event as recorded still names Bin 14 - AR 195-5 2-5b(5).
        Assert.Equal(14, location.StorageLocationId);
        Assert.Equal(14, correction.OriginalReferenceId);
    }

    [Fact]
    public void ALocationCorrectedTwiceEndsAtTheLastIdentifier()
    {
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var first = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);
        var second = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 21, "Shelf B / Bin 21", 3, existing: [first]);

        var effective = new EffectiveItemEvent(location, [first, second]);

        Assert.Equal(21, effective.EffectiveStorageLocationId);
        Assert.Equal("Shelf B / Bin 21", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
    }

    [Fact]
    public void CorrectingACustodyRecipientMovesTheParty_NotOnlyTheName()
    {
        // COC-004. A custody counterparty is a row precisely because the regulation's own
        // examples - an accountable mail number (2-7e), "N/A Custodian Unable to Sign" (3-2g(5)),
        // an organization (2-7c) - are not free text about a person. Correcting the recipient
        // must land on another such row.
        var custody = new CustodyEvent(
            releasedBy: CustodyParty.ForOrganization("902d MI Group"),
            receivedBy: CustodyParty.ForExternalPerson("SMITH, JOHN A.", "SA", "902d MI Group", true),
            purposeOfChangeOfCustody: "Received into evidence room",
            occurredAtLocal: Local,
            recordedAtUtc: Local.ToUniversalTime(),
            recordedByUserId: 1,
            isScrcni: false);

        custody.AssignSequence(100, 1);

        var correction = CorrectTo(custody, nameof(CustodyEvent.ReceivedBy), 55, "JONES, MARY B.", 2);
        var current = EffectiveHistory.LatestOf<CustodyEvent>([custody, correction]);

        Assert.Equal(55, current!.EffectiveReceivedByPartyId);

        // The releasing party was not corrected, so it keeps the row it always named.
        Assert.Equal(custody.ReleasedByPartyId, current.EffectiveReleasedByPartyId);
    }

    [Fact]
    public void CorrectingAFreeTextFieldLeavesTheIdentifierAlone()
    {
        // Correcting the reason a thing was moved does not move it.
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var reasonFix = Correct(location, nameof(LocationEvent.Reason), "Moved to high-value storage", 2);

        var effective = new EffectiveItemEvent(location, [reasonFix]);

        Assert.Equal(14, effective.EffectiveStorageLocationId);
        Assert.Equal(
            "Shelf B / Bin 14", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
    }

    [Fact]
    public void ReferenceProjectionsAreNullOnEventTypesThatHaveNone()
    {
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var effective = new EffectiveItemEvent(location, []);

        Assert.Null(effective.EffectiveReceivedByPartyId);
        Assert.Null(effective.EffectiveReleasedByPartyId);
        Assert.Null(effective.EffectiveReferenceIdOf(nameof(LocationEvent.Reason)));
    }

    [Fact]
    public void ThreeSequentialCorrectionsEachRecordWhatTheyActuallyChanged()
    {
        // AUD-017. Bin 14 -> Bin 19 -> Bin 21 -> High-Value Safe. AR 195-5 1-7c(3) requires an
        // MFR outlining THE ERROR and the corrective action; for the second correction the error
        // was Bin 19, not Bin 14. Reporting it as "Bin 14 -> Bin 21" would describe a change
        // that never happened and drop Bin 19 from the account of what went wrong.
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);

        var first = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);
        var second = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 21, "Shelf B / Bin 21", 3, existing: [first]);
        var third = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 7, "High-Value Safe / Drawer 2", 4, existing: [first, second]);

        // Every correction keeps the ORIGINAL readable (2-5b(5)) ...
        Assert.All([first, second, third], c => Assert.Equal("Shelf B / Bin 14", c.OriginalValue));
        Assert.All([first, second, third], c => Assert.Equal(14, c.OriginalReferenceId));

        // ... and states what IT changed.
        Assert.Equal("Shelf B / Bin 14", first.PreviousEffectiveValue);
        Assert.Equal(14, first.PreviousEffectiveReferenceId);
        Assert.True(first.CorrectsTheOriginalEntry);

        Assert.Equal("Shelf B / Bin 19", second.PreviousEffectiveValue);
        Assert.Equal(19, second.PreviousEffectiveReferenceId);
        Assert.False(second.CorrectsTheOriginalEntry);

        Assert.Equal("Shelf B / Bin 21", third.PreviousEffectiveValue);
        Assert.Equal(21, third.PreviousEffectiveReferenceId);
        Assert.False(third.CorrectsTheOriginalEntry);

        var effective = new EffectiveItemEvent(location, [first, second, third]);
        Assert.Equal(7, effective.EffectiveStorageLocationId);
        Assert.Equal("High-Value Safe / Drawer 2", effective.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)));
        Assert.Equal(3, effective.Corrections.Count);
    }

    [Fact]
    public void ACorrectionThatRestatesTheCurrentValueIsRefused_EvenIfItDiffersFromTheOriginal()
    {
        // Bin 14 -> Bin 19, then "correct" to Bin 19 again. That changes nothing about what the
        // record reads, so it documents nothing - even though Bin 19 differs from the original.
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var first = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

        var ex = Assert.Throws<DomainRuleViolationException>(() => CorrectTo(
            location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 3,
            existing: [first]));

        Assert.Equal("AUD-004", ex.RequirementId);
    }

    [Fact]
    public void RestoringTheOriginalValueIsAValidCorrection()
    {
        // The mirror case. Bin 14 -> Bin 19 was itself the mistake; correcting back to Bin 14
        // changes what the record reads and must be allowed. The old "compare to the original"
        // rule would have refused it as a no-op.
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var first = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);

        var back = CorrectTo(
            location, nameof(LocationEvent.StorageLocationPath), 14, "Shelf B / Bin 14", 3,
            reason: "The first correction was itself in error.", existing: [first]);

        Assert.Equal(19, back.PreviousEffectiveReferenceId);
        Assert.Equal(14, back.CorrectedReferenceId);

        var effective = new EffectiveItemEvent(location, [first, back]);
        Assert.Equal(14, effective.EffectiveStorageLocationId);

        // Both corrections stay on the record.
        Assert.Equal(2, effective.Corrections.Count);
    }

    [Fact]
    public void ABackDatedCorrectionDoesNotTakePrecedenceOverALaterAppendedOne()
    {
        // The effective value follows APPEND ORDER, not a user-supplied occurrence time. If it
        // followed occurrence time, whoever entered a correction last could back-date it to win
        // over one entered before it - choosing the order of the record rather than observing
        // it. Sequence numbers are assigned by the server and cannot be chosen.
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);

        // Appended second (sequence 3) but dated EARLIER than the first correction.
        var appendedFirst = CorrectionFactory.CreateReferenceCorrection(
            location, [], nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19",
            "first entered", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            Local.AddMinutes(30), Local.ToUniversalTime().AddMinutes(30), 1,
            mfrReference: "MFR-1",
            supervisorNotification: SupervisorNotification.OfPerson("RIVERA, LUIS M.", "CW3", null, Local.ToUniversalTime()));
        appendedFirst.AssignSequence(100, 2);

        var appendedSecondButBackDated = CorrectionFactory.CreateReferenceCorrection(
            location, [appendedFirst], nameof(LocationEvent.StorageLocationPath), 21, "Shelf B / Bin 21",
            "entered later, dated earlier", CorrectionCategory.PostAcceptanceAccountabilityRecord,
            Local.AddMinutes(5), Local.ToUniversalTime().AddMinutes(5), 1,
            mfrReference: "MFR-2",
            supervisorNotification: SupervisorNotification.OfPerson("RIVERA, LUIS M.", "CW3", null, Local.ToUniversalTime()));
        appendedSecondButBackDated.AssignSequence(100, 3);

        // Presented in a deliberately misleading order; the projection must not care.
        var effective = new EffectiveItemEvent(location, [appendedSecondButBackDated, appendedFirst]);

        Assert.Equal(21, effective.EffectiveStorageLocationId);
        Assert.Equal(2, effective.Corrections[0].SequenceNumber);
        Assert.Equal(3, effective.Corrections[1].SequenceNumber);
    }

    [Fact]
    public void AChainedCorrectionSummarizesWhatItChangedAndNamesTheOriginalSeparately()
    {
        var location = NewLocationEvent(1, "Shelf B / Bin 14", 14);
        var first = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 19, "Shelf B / Bin 19", 2);
        var second = CorrectTo(location, nameof(LocationEvent.StorageLocationPath), 21, "Shelf B / Bin 21", 3, existing: [first]);

        Assert.Contains("\"Shelf B / Bin 14\" → \"Shelf B / Bin 19\"", first.Summarize(), StringComparison.Ordinal);
        Assert.DoesNotContain("as originally recorded", first.Summarize(), StringComparison.Ordinal);

        Assert.Contains("\"Shelf B / Bin 19\" → \"Shelf B / Bin 21\"", second.Summarize(), StringComparison.Ordinal);
        Assert.Contains("as originally recorded: \"Shelf B / Bin 14\"", second.Summarize(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Shelf B / Bin 14\" → \"Shelf B / Bin 21\"", second.Summarize(), StringComparison.Ordinal);
    }

    // Note: "corrections belonging to another event are ignored" cannot be asserted here.
    // Unpersisted events all carry Id 0, so CorrectsEventId cannot distinguish them in memory.
    // That behaviour is covered in Emc.Application.Tests, where identifiers are real.
}
