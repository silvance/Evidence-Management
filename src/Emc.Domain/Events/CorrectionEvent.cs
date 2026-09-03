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
        int? supervisorNotifiedUserId,
        DateTimeOffset? supervisorNotifiedAtUtc,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, correctedByUserId, notes)
    {
        ArgumentNullException.ThrowIfNull(correctedEvent);

        Reason = Guard.NotBlank(reason, "AUD-004", "Reason for the correction");
        FieldName = Guard.NotBlank(fieldName, "AUD-004", "Corrected field name");

        if (string.Equals(originalValue, correctedValue, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException(
                "AUD-004",
                "A correction must change the recorded value. The original and corrected values "
                + "are identical.");
        }

        CorrectsEventId = correctedEvent.Id;
        CorrectedEvent = correctedEvent;
        OriginalValue = originalValue;
        CorrectedValue = correctedValue;
        Category = category;
        MfrReference = Guard.TrimToNull(mfrReference);
        SupervisorNotifiedUserId = supervisorNotifiedUserId;
        SupervisorNotifiedAtUtc = supervisorNotifiedAtUtc;
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

    /// <summary>AR 195-5 1-7c(3) - the corrective action documented.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Which correction workflow this belongs to. See <see cref="CorrectionCategory"/>.</summary>
    public CorrectionCategory Category { get; private set; }

    /// <summary>AR 195-5 1-7c(3) - the MFR filed with the DA Form 4137, copy in the case file.</summary>
    public string? MfrReference { get; private set; }

    /// <summary>AR 195-5 1-7c(3) - the supervisor informed immediately.</summary>
    public int? SupervisorNotifiedUserId { get; private set; }

    public DateTimeOffset? SupervisorNotifiedAtUtc { get; private set; }

    /// <summary>
    /// AR 195-5 1-7c(3) requires supervisor notification and an MFR when a CUSTODIAN finds an
    /// incorrect entry in the accountability record. It does NOT govern a submitting agent
    /// correcting a draft under 2-3g, nor verification of an OCR transcription - so the check
    /// applies only to <see cref="CorrectionCategory.PostAcceptanceAccountabilityRecord"/>.
    /// </summary>
    public bool RequiresParagraph1_7c3Documentation
        => Category == CorrectionCategory.PostAcceptanceAccountabilityRecord;

    public bool SatisfiesParagraph1_7c3
        => !RequiresParagraph1_7c3Documentation
           || (MfrReference is not null && SupervisorNotifiedUserId is not null);

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
        yield return new(
            "SupervisorNotifiedAtUtc",
            SupervisorNotifiedAtUtc?.UtcDateTime.ToString("O", null));
    }

    public override string Summarize()
        => $"Correction to event #{CorrectsEventId}: {FieldName} "
           + $"\"{OriginalValue}\" → \"{CorrectedValue}\" — {Reason}";
}

/// <summary>
/// Creates corrections, deriving the original value from the target event so a caller cannot
/// state it. The only supported way to build a <see cref="CorrectionEvent"/>.
/// </summary>
public static class CorrectionFactory
{
    public static CorrectionEvent Create(
        ItemEvent correctedEvent,
        string fieldName,
        string? correctedValue,
        string reason,
        CorrectionCategory category,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int correctedByUserId,
        string? mfrReference = null,
        int? supervisorNotifiedUserId = null,
        DateTimeOffset? supervisorNotifiedAtUtc = null,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(correctedEvent);

        // AUD-014. The original comes from the stored event, not from the caller. Also validates
        // that the field is correctable on this event type at all.
        var originalValue = correctedEvent.OriginalValueOf(fieldName);

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
            supervisorNotifiedUserId,
            supervisorNotifiedAtUtc,
            notes);
    }
}
