using Emc.Domain.Common;
using Emc.Domain.Events;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// The append-only event model, the correction pattern, and the hash chain.
/// Requirements: AUD-001, AUD-003, AUD-004, AUD-005, AUD-008, AUD-011, COC-003 .. COC-006.
/// </summary>
public class EventAndCorrectionTests
{
    private static readonly DateTimeOffset Local =
        new(2026, 9, 3, 9, 15, 0, TimeSpan.FromHours(-4));

    private static CustodyEvent NewCustodyEvent(string receivedByName = "JONES, MARY B.")
        => new(
            releasedBy: CustodyParty.ForExternalPerson("SMITH, JOHN A.", "SA", "902d MI Group", true),
            receivedBy: CustodyParty.ForExternalPerson(receivedByName, "SA", "902d MI Group", true),
            purposeOfChangeOfCustody: "Received into evidence room",
            occurredAtLocal: Local,
            recordedAtUtc: Local.ToUniversalTime(),
            recordedByUserId: 1,
            isScrcni: false);

    [Fact]
    public void EventsRecordBothOccurrenceAndSystemEntryTime()
    {
        // AUD-011. The DA Form 4137 and the ledger record LOCAL time ("03 SEP 26 09:15"), and
        // back-dated entry is legitimate - a custody transfer at 0200 recorded at 0800. An
        // auditor must be able to see both.
        var recordedAt = Local.ToUniversalTime().AddHours(6);

        var custodyEvent = new CustodyEvent(
            releasedBy: CustodyParty.ForOrganization("902d MI Group"),
            receivedBy: CustodyParty.ForOrganization("USACIL"),
            purposeOfChangeOfCustody: "Forwarded for forensic examination",
            occurredAtLocal: Local,
            recordedAtUtc: recordedAt,
            recordedByUserId: 1,
            isScrcni: true);

        Assert.Equal(Local, custodyEvent.OccurredAtLocal);
        Assert.Equal(TimeSpan.FromHours(-4), custodyEvent.OccurredAtOffset);
        Assert.Equal(Local.ToUniversalTime(), custodyEvent.OccurredAtUtc);
        Assert.Equal(recordedAt, custodyEvent.RecordedAtUtc);
        Assert.NotEqual(custodyEvent.OccurredAtUtc, custodyEvent.RecordedAtUtc);
    }

    [Fact]
    public void ScrcniIsAnnotatedInThePurposeOfChangeOfCustody()
    {
        // AR 195-5 2-3e / 2-3f: "the evidence custodian will annotate the Purpose of Change of
        // Custody on the DA Form 4137 with the acronym SCRCNI (sealed container received;
        // contents not inventoried)" (COC-005).
        var custodyEvent = new CustodyEvent(
            releasedBy: CustodyParty.ForOrganization("902d MI Group"),
            receivedBy: CustodyParty.ForOrganization("USACIL"),
            purposeOfChangeOfCustody: "Forwarded for forensic examination",
            occurredAtLocal: Local,
            recordedAtUtc: Local.ToUniversalTime(),
            recordedByUserId: 1,
            isScrcni: true);

        Assert.Contains("SCRCNI", custodyEvent.PurposeForForm, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAccountableMailNumberIsAValidCustodyParty()
    {
        // AR 195-5 2-7e: "The evidence custodian will only enter the registered or other
        // accountable mail number in the Received by block of the chain of custody section of the
        // DA Form 4137."
        //
        // This is why custody parties are not a foreign key to User (COC-004, COC-006).
        var party = CustodyParty.ForAccountableMailNumber("RE 123 456 789 US", "USPS Registered Mail");

        Assert.Equal(CustodyPartyKind.AccountableMailNumber, party.Kind);
        Assert.Equal("RE 123 456 789 US", party.DisplayName);
    }

    [Fact]
    public void CustodianUnableToSignIsAValidCustodyParty()
    {
        // AR 195-5 3-2g(5): on the death or incapacity of the primary custodian "the Released By
        // block of each DA Form 4137 will be annotated N/A Custodian Unable to Sign" (COC-007).
        var party = CustodyParty.CustodianUnableToSign();

        Assert.Equal("N/A Custodian Unable to Sign", party.DisplayName);
    }

    [Fact]
    public void ABreachRequiresAPurposeAndAnMfr()
    {
        // AR 195-5 2-3e: "Any breach of a sealed evidence container by the evidence custodian will
        // be annotated on the DA Form 4137 ... the evidence custodian will prepare a MFR
        // describing the purpose of the breach. The MFR will then be affixed to the original DA
        // Form 4137 as a permanent attachment" (DOC-007).
        var ex = Assert.Throws<DomainRuleViolationException>(() => new SealEvent(
            action: SealAction.Breached,
            performedByName: "SMITH, JOHN A.",
            occurredAtLocal: Local,
            recordedAtUtc: Local.ToUniversalTime(),
            recordedByUserId: 1,
            purposeOfBreach: null,
            mfrReference: null));

        Assert.Equal("DOC-007", ex.RequirementId);
    }

    [Fact]
    public void ACorrectionMustStateAReasonAndActuallyChangeTheValue()
    {
        // AUD-004, invariant I-15. AR 195-5 1-7c(3) requires the corrective action to be
        // documented, and a "correction" that changes nothing documents nothing.
        var corrected = NewCustodyEvent();

        Assert.Throws<DomainRuleViolationException>(() => new CorrectionEvent(
            corrected, "ReceivedBy", "Smith", "Jones", "   ",
            Local, Local.ToUniversalTime(), 1, null, null, null));

        Assert.Throws<DomainRuleViolationException>(() => new CorrectionEvent(
            corrected, "ReceivedBy", "Smith", "Smith", "Transcription error",
            Local, Local.ToUniversalTime(), 1, null, null, null));
    }

    [Fact]
    public void ACorrectionPreservesTheOriginalValue()
    {
        // AR 195-5 2-5b(5): "Erroneous entries will be voided with one line drawn through the
        // entry (so it may still be read) and initialed by the custodian. No liquid correction
        // type products, correction tape, stick-on labels, or erasures are authorized."
        //
        // The software analogue: the original is retained verbatim (AUD-003, AUD-004).
        var corrected = NewCustodyEvent("SMITH, JOHN A.");

        var correction = new CorrectionEvent(
            correctedEvent: corrected,
            fieldName: "ReceivedBy",
            originalValue: "SMITH, JOHN A.",
            correctedValue: "JONES, MARY B.",
            reason: "Transcription error; the DA Form 4137 shows JONES, MARY B.",
            occurredAtLocal: Local,
            recordedAtUtc: Local.ToUniversalTime(),
            correctedByUserId: 17,
            mfrReference: "MFR-2026-014",
            supervisorNotifiedUserId: 4,
            supervisorNotifiedAtUtc: Local.ToUniversalTime().AddMinutes(9));

        Assert.Equal("SMITH, JOHN A.", correction.OriginalValue);
        Assert.Equal("JONES, MARY B.", correction.CorrectedValue);
        Assert.Equal(17, correction.RecordedByUserId);
        Assert.True(correction.SatisfiesParagraph1_7c3);
    }

    [Fact]
    public void ACorrectionWithoutAnMfrOrSupervisorNotificationIsFlagged()
    {
        // AR 195-5 1-7c(3) requires BOTH: immediate notification of the responsible CI supervisor
        // AND an MFR outlining the error and corrective action. EMC records the correction and
        // flags the shortfall rather than blocking, because whether a given field-level
        // correction reaches that threshold is local policy (AUD-005).
        var correction = new CorrectionEvent(
            NewCustodyEvent(), "Notes", "old", "new", "Typo",
            Local, Local.ToUniversalTime(), 1, null, null, null);

        Assert.False(correction.SatisfiesParagraph1_7c3);
    }

    [Fact]
    public void HashChain_DetectsAModifiedEvent()
    {
        // AUD-008. Triggers stop casual modification; the chain makes out-of-band modification
        // DETECTABLE BY ANY READER, which is the achievable goal.
        var first = NewCustodyEvent();
        first.AssignSequence(100, 1);
        EventHashChain.Seal(first, null);

        var tampered = new TamperedEventProbe(first);

        Assert.NotEqual(
            first.EventHash,
            EventHashChain.ComputeHash(tampered.Modified, tampered.Modified.PreviousEventHash));
    }

    [Fact]
    public void HashChain_VerifiesAnIntactChain()
    {
        var events = SealedChain(3);
        var result = EventHashChain.Verify(events);

        Assert.True(result.IsIntact);
        Assert.Equal(3, result.EventsChecked);
    }

    [Fact]
    public void HashChain_DetectsARemovedEvent()
    {
        // A removed row shows up two ways: a sequence gap, and a broken link at the event that
        // followed it. Both are reported so the reader can see what happened.
        var events = SealedChain(3);
        var withGap = new[] { events[0], events[2] };

        var result = EventHashChain.Verify(withGap);

        Assert.False(result.IsIntact);
        Assert.Contains(result.Problems, p => p.Kind == ChainProblemKind.SequenceGap);
        Assert.Contains(result.Problems, p => p.Kind == ChainProblemKind.BrokenLink);
    }

    [Fact]
    public void HashChain_DistinguishesNullFromEmpty()
    {
        // A value blanked to "" must not hash the same as a value that was always null, or a
        // field could be erased without breaking the chain.
        var withNull = new StatusEvent(
            AccountabilityStatus.Draft, AccountabilityStatus.Acquired, "reason",
            Local, Local.ToUniversalTime(), 1, notes: null);

        var withEmpty = new StatusEvent(
            AccountabilityStatus.Draft, AccountabilityStatus.Acquired, "reason",
            Local, Local.ToUniversalTime(), 1, notes: "x");

        withNull.AssignSequence(1, 1);
        withEmpty.AssignSequence(1, 1);

        Assert.NotEqual(
            EventHashChain.ComputeHash(withNull, null),
            EventHashChain.ComputeHash(withEmpty, null));
    }

    [Fact]
    public void AnEventCannotBeSequencedOrHashedTwice()
    {
        var custodyEvent = NewCustodyEvent();
        custodyEvent.AssignSequence(1, 1);
        EventHashChain.Seal(custodyEvent, null);

        Assert.Throws<AppendOnlyViolationException>(() => custodyEvent.AssignSequence(1, 2));
        Assert.Throws<AppendOnlyViolationException>(() => EventHashChain.Seal(custodyEvent, "abc"));
    }

    private static List<ItemEvent> SealedChain(int count)
    {
        var events = new List<ItemEvent>();
        string? previous = null;

        for (var i = 1; i <= count; i++)
        {
            var statusEvent = new StatusEvent(
                AccountabilityStatus.Draft, AccountabilityStatus.Acquired, $"reason {i}",
                Local.AddMinutes(i), Local.ToUniversalTime().AddMinutes(i), 1);

            statusEvent.AssignSequence(100, i);
            EventHashChain.Seal(statusEvent, previous);
            previous = statusEvent.EventHash;
            events.Add(statusEvent);
        }

        return events;
    }

    /// <summary>
    /// Stands in for a row altered outside the application - the case the SaveChanges guard
    /// cannot see and the database triggers are meant to catch. Recomputing the hash over the
    /// altered content is what makes it detectable.
    /// </summary>
    private sealed class TamperedEventProbe
    {
        public TamperedEventProbe(CustodyEvent original)
        {
            Modified = new CustodyEvent(
                releasedBy: original.ReleasedBy,
                receivedBy: CustodyParty.ForExternalPerson("ALTERED, NAME", "SA", "Unit", true),
                purposeOfChangeOfCustody: original.PurposeOfChangeOfCustody,
                occurredAtLocal: original.OccurredAtLocal,
                recordedAtUtc: original.RecordedAtUtc,
                recordedByUserId: original.RecordedByUserId,
                isScrcni: original.IsScrcni);

            Modified.AssignSequence(original.EvidenceItemId, original.SequenceNumber);
        }

        public CustodyEvent Modified { get; }
    }
}

/// <summary>
/// Regression tests for AccountabilityTime.
///
/// The per-item hash chain covers timestamps, so if a stored timestamp comes back with less
/// precision than the value that was hashed, verification reports tampering that never happened.
/// Storage precision varies by column type and provider (SQL Server datetimeoffset keeps 100ns;
/// datetime2(3) and EF's DateTimeOffsetToBinaryConverter keep milliseconds), so accountability
/// timestamps are normalized to whole milliseconds at construction instead.
///
/// This was found by a web-host test using real wall-clock times; the earlier tests happened to
/// use timestamps with no sub-millisecond component and so passed for the wrong reason.
/// </summary>
public class AccountabilityTimeTests
{
    [Fact]
    public void EventTimestampsAreNormalizedToWholeMilliseconds()
    {
        var ragged = new DateTimeOffset(2026, 9, 3, 9, 15, 30, TimeSpan.FromHours(-4))
            .AddTicks(1234567);

        var statusEvent = new StatusEvent(
            AccountabilityStatus.Draft, AccountabilityStatus.Acquired, "reason",
            ragged, ragged.ToUniversalTime(), 1);

        Assert.Equal(0, statusEvent.OccurredAtUtc.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(0, statusEvent.OccurredAtLocal.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(0, statusEvent.RecordedAtUtc.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void NormalizationPreservesTheOffset()
    {
        // The offset must survive: AR 195-5 records LOCAL time on the DA Form 4137 and in the
        // ledger ("03 SEP 26 09:15"), so EMC must be able to render what the paper says.
        var local = new DateTimeOffset(2026, 9, 3, 9, 15, 30, TimeSpan.FromHours(-4)).AddTicks(9999);

        var normalized = AccountabilityTime.Normalize(local);

        Assert.Equal(TimeSpan.FromHours(-4), normalized.Offset);
        Assert.Equal(9, normalized.Hour);
        Assert.Equal(15, normalized.Minute);
    }

    [Fact]
    public void AHashSurvivesAMillisecondPrecisionRoundTrip()
    {
        // Simulates storage that keeps only milliseconds. The hash must be unchanged, or the
        // chain would break on read.
        var ragged = DateTimeOffset.UtcNow;

        var original = new StatusEvent(
            AccountabilityStatus.Draft, AccountabilityStatus.Acquired, "reason",
            ragged, ragged, 1);

        original.AssignSequence(1, 1);
        var hash = EventHashChain.ComputeHash(original, null);

        var truncated = new DateTimeOffset(
            original.OccurredAtLocal.Ticks
                - (original.OccurredAtLocal.Ticks % TimeSpan.TicksPerMillisecond),
            original.OccurredAtLocal.Offset);

        var roundTripped = new StatusEvent(
            AccountabilityStatus.Draft, AccountabilityStatus.Acquired, "reason",
            truncated, truncated, 1);

        roundTripped.AssignSequence(1, 1);

        Assert.Equal(hash, EventHashChain.ComputeHash(roundTripped, null));
    }

    [Fact]
    public void NullTimestampsNormalizeToNull()
        => Assert.Null(AccountabilityTime.Normalize((DateTimeOffset?)null));
}
