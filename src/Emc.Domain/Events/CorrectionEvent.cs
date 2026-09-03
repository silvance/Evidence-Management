using Emc.Domain.Common;

namespace Emc.Domain.Events;

/// <summary>
/// A correction to ONE FIELD of a previously recorded accountability event.
///
/// This is the software analogue of how AR 195-5 requires errors to be handled on paper:
///
///   2-5b(5)  "Erroneous entries will be voided with one line drawn through the entry (SO IT MAY
///            STILL BE READ) and initialed by the custodian. No liquid correction type products,
///            correction tape, stick-on labels, or erasures are authorized."
///   1-7c(3)  On finding an incorrect entry the custodian "will IMMEDIATELY INFORM the
///            responsible ... CI supervisor" and prepare an MFR outlining the error and
///            corrective action, filed with the DA Form 4137 with a copy in the case file.
///
/// Three properties of this design matter, and the earlier one had none of them:
///
///   1. FIELD-LEVEL, not event-level. Correcting one field leaves the rest of the event standing.
///      The earlier model marked the whole event superseded, which made a corrected location
///      event disappear from the current-location projection entirely - so correcting "Bin 14" to
///      "Bin 19" left the item with NO recorded location at all.
///
///   2. The ORIGINAL VALUE IS DERIVED BY THE SERVER from the corrected event's
///      <see cref="ItemEvent.CorrectableFields"/>. It is never accepted from the client. An audit
///      record whose "original value" came from a form post would be worthless (AUD-014).
///
///   3. BACKWARD REFERENCE ONLY. The corrected event is never touched, so the accountability
///      tables need no UPDATE path at all. Correction status is derived from the existence of
///      these records (AUD-002).
///
/// A field may be corrected more than once, and a correction may itself be corrected: the
/// effective value is simply the most recent correction for that field
/// (see <see cref="EffectiveItemEvent"/>). Nothing is ever hidden.
///
/// Requirements: AUD-003, AUD-004, AUD-005, AUD-014, AUD-015.
/// </summary>
public class CorrectionEvent : ItemEvent
{
    private CorrectionEvent() { }

    /// <summary>
    /// Constructs a correction. <paramref name="originalValue"/> is supplied by
    /// <see cref="CorrectionFactory"/> from the corrected event, never by a caller outside the
    /// domain, so the recorded original cannot be falsified.
    /// </summary>
    internal CorrectionEvent(
        ItemEvent correctedEvent,
        string fieldName,
        string? originalValue,
        string? correctedValue,
        string reason,
        CorrectionCategory category,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int correctedByUserId,
        string? mfrReference,
        SupervisorNotification? supervisorNotification,
        CorrectableFieldReference referenceKind,
        int? originalReferenceId,
        int? correctedReferenceId,
        string? previousEffectiveValue,
        int? previousEffectiveReferenceId,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, correctedByUserId, notes)
    {
        ArgumentNullException.ThrowIfNull(correctedEvent);

        Reason = Guard.NotBlank(reason, "AUD-004", "Reason for the correction");
        FieldName = Guard.NotBlank(fieldName, "AUD-004", "Corrected field name");

        // Compared against the value as the record CURRENTLY READS, not against the value as
        // first recorded. Correcting Bin 14 to Bin 19 and then Bin 19 to Bin 19 changes nothing
        // and must be refused, even though Bin 19 differs from the original Bin 14. Judged on the
        // IDENTIFIER for a reference field: two storage locations in different rooms can display
        // the same path, and renaming one would otherwise make a genuine move look like a no-op.
        var changesNothing = referenceKind == CorrectableFieldReference.None
            ? string.Equals(previousEffectiveValue, correctedValue, StringComparison.Ordinal)
            : previousEffectiveReferenceId == correctedReferenceId;

        if (changesNothing)
        {
            throw new DomainRuleViolationException(
                "AUD-004",
                "A correction must change the recorded value. The original and corrected values "
                + "are identical.");
        }

        if (referenceKind == CorrectableFieldReference.None)
        {
            if (correctedReferenceId is not null || originalReferenceId is not null)
            {
                throw new DomainRuleViolationException(
                    "AUD-016",
                    $"'{fieldName}' is free text on a {correctedEvent.Kind} event and carries no "
                    + "identifier. A correction to it must not name a row.");
            }
        }
        else if (correctedReferenceId is null)
        {
            throw new DomainRuleViolationException(
                "AUD-016",
                $"'{fieldName}' names a {referenceKind}. A correction to it must name the "
                + "replacement row, not only new display text, or the record's own projections "
                + "would continue to point at the row that was corrected away.");
        }

        CorrectsEventId = correctedEvent.Id;
        CorrectedEvent = correctedEvent;
        OriginalValue = originalValue;
        CorrectedValue = correctedValue;
        Category = category;
        MfrReference = Guard.TrimToNull(mfrReference);
        SupervisorNotifiedUserId = supervisorNotification?.UserId;
        SupervisorNotifiedName = supervisorNotification?.PrintedName;
        SupervisorNotifiedGradeOrTitle = supervisorNotification?.GradeOrTitle;
        SupervisorNotifiedOrganization = supervisorNotification?.Organization;
        SupervisorNotifiedAtUtc = supervisorNotification?.NotifiedAtUtc;

        // AUD-005, enforced rather than flagged. AR 195-5 1-7c(3) makes the supervisor
        // notification and the MFR part of correcting an incorrect entry in the accountability
        // record, not an optional follow-up: the custodian "will immediately inform" the
        // supervisor and "will prepare" the MFR. An earlier version recorded the correction
        // anyway and showed a warning, which let the accountability record be changed before the
        // documentation the regulation attaches to that change existed. The checks above run
        // first so a caller learns about a no-op or a mis-typed field before being asked for an
        // MFR it does not yet have.
        if (category == CorrectionCategory.PostAcceptanceAccountabilityRecord)
        {
            if (MfrReference is null)
            {
                throw new DomainRuleViolationException(
                    "AUD-005",
                    "AR 195-5 para 1-7c(3): a correction to an accepted accountability record "
                    + "requires an MFR outlining the error and the corrective action taken, filed "
                    + "with the DA Form 4137 with a copy in the case file. Record the MFR "
                    + "reference.");
            }

            if (supervisorNotification is null)
            {
                throw new DomainRuleViolationException(
                    "AUD-005",
                    "AR 195-5 para 1-7c(3): the custodian who finds an incorrect entry will "
                    + "immediately inform the responsible CI supervisor. Record who was informed "
                    + "and when. The supervisor need not hold an EMC account.");
            }
        }

        ReferenceKind = referenceKind;
        OriginalReferenceId = originalReferenceId;
        CorrectedReferenceId = correctedReferenceId;
        PreviousEffectiveValue = previousEffectiveValue;
        PreviousEffectiveReferenceId = previousEffectiveReferenceId;
    }

    public override ItemEventKind Kind => ItemEventKind.Correction;

    /// <summary>The event this corrects. A backward reference; the target is never modified.</summary>
    public int CorrectsEventId { get; private set; }

    public ItemEvent? CorrectedEvent { get; private set; }

    /// <summary>The single field corrected. Validated against the target's correctable fields.</summary>
    public string FieldName { get; private set; } = string.Empty;

    /// <summary>
    /// The value as originally recorded, derived by the server. AR 195-5 2-5b(5)'s struck-through
    /// entry must still be readable, and an auditor must always be able to see the original.
    /// </summary>
    public string? OriginalValue { get; private set; }

    public string? CorrectedValue { get; private set; }

    /// <summary>
    /// The value THIS correction changed - the field as the record read immediately before it,
    /// which is <see cref="OriginalValue"/> only when this is the first correction to the field.
    ///
    /// Both are needed and they answer different questions. AR 195-5 2-5b(5) keeps the ORIGINAL
    /// entry readable, so an auditor must always be able to see what was first written. But
    /// AR 195-5 1-7c(3) requires an MFR outlining THE ERROR and THE CORRECTIVE ACTION TAKEN, and
    /// for the second correction in a chain the error was not the original entry: correcting
    /// Bin 14 to Bin 19 and then to Bin 21 is a correction OF BIN 19. Reporting it as
    /// "Bin 14 to Bin 21" would describe a change that never happened and would silently drop
    /// Bin 19 from the account of what went wrong.
    /// </summary>
    public string? PreviousEffectiveValue { get; private set; }

    /// <summary>The identifier <see cref="PreviousEffectiveValue"/> named, for a reference field.</summary>
    public int? PreviousEffectiveReferenceId { get; private set; }

    /// <summary>
    /// True when this correction changed the field's first recorded value rather than an earlier
    /// correction's. Derived, so the two cannot drift apart.
    /// </summary>
    public bool CorrectsTheOriginalEntry
        => ReferenceKind == CorrectableFieldReference.None
            ? string.Equals(PreviousEffectiveValue, OriginalValue, StringComparison.Ordinal)
            : PreviousEffectiveReferenceId == OriginalReferenceId;

    /// <summary>AR 195-5 1-7c(3) - the corrective action documented.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Which correction workflow this belongs to. See <see cref="CorrectionCategory"/>.</summary>
    public CorrectionCategory Category { get; private set; }

    /// <summary>AR 195-5 1-7c(3) - the MFR filed with the DA Form 4137, copy in the case file.</summary>
    public string? MfrReference { get; private set; }

    /// <summary>
    /// AR 195-5 1-7c(3) - the supervisor informed immediately. Stored flat; see
    /// <see cref="SupervisorNotification"/> for why the user link is optional and the printed
    /// particulars are not.
    /// </summary>
    public int? SupervisorNotifiedUserId { get; private set; }

    public string? SupervisorNotifiedName { get; private set; }

    public string? SupervisorNotifiedGradeOrTitle { get; private set; }

    public string? SupervisorNotifiedOrganization { get; private set; }

    public DateTimeOffset? SupervisorNotifiedAtUtc { get; private set; }

    /// <summary>The notification as a whole, or null when none was recorded.</summary>
    public SupervisorNotification? SupervisorNotification
        => Events.SupervisorNotification.FromStored(
            SupervisorNotifiedUserId,
            SupervisorNotifiedName,
            SupervisorNotifiedGradeOrTitle,
            SupervisorNotifiedOrganization,
            SupervisorNotifiedAtUtc);

    /// <summary>
    /// AR 195-5 1-7c(3) requires supervisor notification and an MFR when a CUSTODIAN finds an
    /// incorrect entry in the accountability record. It does NOT govern a submitting agent
    /// correcting a draft under 2-3g, nor verification of an OCR transcription - so the
    /// requirement applies only to <see cref="CorrectionCategory.PostAcceptanceAccountabilityRecord"/>,
    /// where the constructor enforces it. A stored correction of that category always carries
    /// both.
    /// </summary>
    public bool RequiresParagraph1_7c3Documentation
        => Category == CorrectionCategory.PostAcceptanceAccountabilityRecord;

    /// <summary>
    /// What kind of row this field names, or <see cref="CorrectableFieldReference.None"/> for
    /// free text.
    /// </summary>
    public CorrectableFieldReference ReferenceKind { get; private set; }

    /// <summary>
    /// The identifier as originally recorded, derived by the server from the corrected event.
    /// Null for a free-text field.
    /// </summary>
    public int? OriginalReferenceId { get; private set; }

    /// <summary>
    /// The replacement identifier. Null for a free-text field; required otherwise. This is what
    /// keeps a corrected location or custody party a resolvable row rather than a string
    /// (AUD-016).
    /// </summary>
    public int? CorrectedReferenceId { get; private set; }

    /// <summary>True when this correction replaces a row rather than free text.</summary>
    public bool IsReferenceCorrection => ReferenceKind != CorrectableFieldReference.None;

    public override IReadOnlyDictionary<string, string?> CorrectableFields
        => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Reason)] = Reason,
            [nameof(Notes)] = Notes
        };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("CorrectsEventId", CorrectsEventId.ToString("D", null));
        yield return new("FieldName", FieldName);
        yield return new("OriginalValue", OriginalValue);
        yield return new("CorrectedValue", CorrectedValue);
        yield return new("Reason", Reason);
        yield return new("Category", Category.ToString());
        yield return new("MfrReference", MfrReference);
        yield return new("SupervisorNotifiedUserId", SupervisorNotifiedUserId?.ToString("D", null));
        yield return new("SupervisorNotifiedName", SupervisorNotifiedName);
        yield return new("SupervisorNotifiedGradeOrTitle", SupervisorNotifiedGradeOrTitle);
        yield return new("SupervisorNotifiedOrganization", SupervisorNotifiedOrganization);
        yield return new(
            "SupervisorNotifiedAtUtc",
            SupervisorNotifiedAtUtc?.UtcDateTime.ToString("O", null));
        yield return new("ReferenceKind", ReferenceKind.ToString());
        yield return new("OriginalReferenceId", OriginalReferenceId?.ToString("D", null));
        yield return new("CorrectedReferenceId", CorrectedReferenceId?.ToString("D", null));
        yield return new("PreviousEffectiveValue", PreviousEffectiveValue);
        yield return new(
            "PreviousEffectiveReferenceId", PreviousEffectiveReferenceId?.ToString("D", null));
    }

    /// <summary>
    /// Reads as the change this correction actually made. For a second or later correction to the
    /// same field that is not the original entry, so the original is named separately rather than
    /// being presented as the value this correction replaced.
    /// </summary>
    public override string Summarize()
        => CorrectsTheOriginalEntry
            ? $"Correction to event #{CorrectsEventId}: {FieldName} "
              + $"\"{OriginalValue}\" → \"{CorrectedValue}\" — {Reason}"
            : $"Correction to event #{CorrectsEventId}: {FieldName} "
              + $"\"{PreviousEffectiveValue}\" → \"{CorrectedValue}\" "
              + $"(as originally recorded: \"{OriginalValue}\") — {Reason}";
}

/// <summary>
/// Creates corrections. The only supported way to build a <see cref="CorrectionEvent"/>.
///
/// Every caller passes the corrections ALREADY RECORDED against the target event. From those and
/// the event itself the factory derives what the caller may not state: the value as originally
/// recorded (AUD-014) and the value as the record read immediately before this correction
/// (AUD-017). An incomplete set of existing corrections would make the second of those wrong, so
/// the application service loads all of them.
/// </summary>
public static class CorrectionFactory
{
    /// <summary>
    /// Corrects a FREE-TEXT field. Rejects a field that names a row: correcting a location or a
    /// custody party by typing new text would leave every projection pointing at the row that was
    /// corrected away, so those go through <see cref="CreateReferenceCorrection"/> instead.
    /// </summary>
    public static CorrectionEvent Create(
        ItemEvent correctedEvent,
        IEnumerable<CorrectionEvent> existingCorrections,
        string fieldName,
        string? correctedValue,
        string reason,
        CorrectionCategory category,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int correctedByUserId,
        string? mfrReference = null,
        SupervisorNotification? supervisorNotification = null,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(correctedEvent);
        ArgumentNullException.ThrowIfNull(existingCorrections);

        // AUD-014. The original comes from the stored event, not from the caller. Also validates
        // that the field is correctable on this event type at all.
        var originalValue = correctedEvent.OriginalValueOf(fieldName);
        var referenceKind = correctedEvent.ReferenceKindOf(fieldName);

        // AUD-017. What this correction changes is the field AS IT NOW READS, after any earlier
        // corrections - derived here from the record, never stated by the caller.
        var asItReads = new EffectiveItemEvent(correctedEvent, existingCorrections);
        var previousEffectiveValue = asItReads.EffectiveValueOf(fieldName);

        if (referenceKind != CorrectableFieldReference.None)
        {
            throw new DomainRuleViolationException(
                "AUD-016",
                $"'{fieldName}' names a {referenceKind} on a {correctedEvent.Kind} event. Correct "
                + "it by naming the replacement row, not by supplying replacement text.");
        }

        return new CorrectionEvent(
            correctedEvent,
            fieldName,
            originalValue,
            Guard.TrimToNull(correctedValue),
            reason,
            category,
            occurredAtLocal,
            recordedAtUtc,
            correctedByUserId,
            mfrReference,
            supervisorNotification,
            CorrectableFieldReference.None,
            originalReferenceId: null,
            correctedReferenceId: null,
            previousEffectiveValue,
            previousEffectiveReferenceId: null,
            notes);
    }

    /// <summary>
    /// Corrects a field that names a row - an item's storage location, or a party to a change of
    /// custody.
    ///
    /// BOTH halves are server-derived, and that is the point. The original identifier and text
    /// come from the stored event (AUD-014). <paramref name="correctedDisplayText"/> must be read
    /// from the REPLACEMENT ROW by the caller, never from a form field, so the text and the
    /// identifier cannot disagree: a correction reading "Shelf B / Bin 19" while pointing at
    /// Bin 21 would be worse than no correction at all.
    ///
    /// The caller is also responsible for checking that the replacement row is a legitimate
    /// target - for a storage location, that it exists and belongs to the same evidence room
    /// (LOC-004). The domain cannot see the other rows.
    /// </summary>
    public static CorrectionEvent CreateReferenceCorrection(
        ItemEvent correctedEvent,
        IEnumerable<CorrectionEvent> existingCorrections,
        string fieldName,
        int correctedReferenceId,
        string correctedDisplayText,
        string reason,
        CorrectionCategory category,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int correctedByUserId,
        string? mfrReference = null,
        SupervisorNotification? supervisorNotification = null,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(correctedEvent);
        ArgumentNullException.ThrowIfNull(existingCorrections);

        var originalValue = correctedEvent.OriginalValueOf(fieldName);
        var referenceKind = correctedEvent.ReferenceKindOf(fieldName);

        // AUD-017. See Create.
        var asItReads = new EffectiveItemEvent(correctedEvent, existingCorrections);
        var previousEffectiveValue = asItReads.EffectiveValueOf(fieldName);
        var previousEffectiveReferenceId = asItReads.EffectiveReferenceIdOf(fieldName);

        if (referenceKind == CorrectableFieldReference.None)
        {
            throw new DomainRuleViolationException(
                "AUD-016",
                $"'{fieldName}' is free text on a {correctedEvent.Kind} event and names no row.");
        }

        return new CorrectionEvent(
            correctedEvent,
            fieldName,
            originalValue,
            Guard.NotBlank(correctedDisplayText, "AUD-016", "Corrected display text"),
            reason,
            category,
            occurredAtLocal,
            recordedAtUtc,
            correctedByUserId,
            mfrReference,
            supervisorNotification,
            referenceKind,
            correctedEvent.OriginalReferenceIdOf(fieldName),
            correctedReferenceId,
            previousEffectiveValue,
            previousEffectiveReferenceId,
            notes);
    }
}
