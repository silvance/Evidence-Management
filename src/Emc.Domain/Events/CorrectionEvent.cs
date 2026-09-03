using Emc.Domain.Common;

namespace Emc.Domain.Events;

/// <summary>
/// A correction to a previously recorded accountability event.
///
/// This is the software analogue of how AR 195-5 requires errors to be handled on paper:
///
///   2-5b(5)  "Erroneous entries will be voided with one line drawn through the entry (SO IT MAY
///            STILL BE READ) and initialed by the custodian. No liquid correction type products,
///            correction tape, stick-on labels, or erasures are authorized to correct erroneous
///            entries."
///   1-7c(3)  On finding an incorrect entry the custodian "will IMMEDIATELY INFORM the
///            responsible ... CI supervisor" and "will also prepare a MFR outlining the error and
///            corrective action taken. The original will be filed with the proper DA Form 4137 ...
///            A copy of the MFR will be placed in the proper law enforcement case file."
///
/// So a correction:
///   - never rewrites the original (the original stays readable — 2-5b(5));
///   - is attributable to the correcting user (initialed — 2-5b(5));
///   - carries a reason;
///   - records the MFR reference and the supervisor notified (1-7c(3)).
///
/// The corrected event is marked superseded; it is never hidden. The UI shows the corrected
/// value marked "Corrected", with the original one click away (AUD-006).
///
/// Requirements: AUD-003, AUD-004, AUD-005. Invariant I-15.
/// </summary>
public class CorrectionEvent : ItemEvent
{
    private CorrectionEvent() { }

    public CorrectionEvent(
        ItemEvent correctedEvent,
        string fieldName,
        string? originalValue,
        string? correctedValue,
        string reason,
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

        if (correctedEvent is CorrectionEvent { SupersededByEventId: not null })
        {
            throw new DomainRuleViolationException(
                "AUD-003",
                "That correction has itself already been superseded. Correct the most recent "
                + "superseding entry instead.");
        }

        // Invariant I-15 / AUD-004. AR 195-5 1-7c(3) requires the corrective action to be
        // documented; a correction without a stated reason would not satisfy that.
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
        MfrReference = Guard.TrimToNull(mfrReference);
        SupervisorNotifiedUserId = supervisorNotifiedUserId;
        SupervisorNotifiedAtUtc = supervisorNotifiedAtUtc;
    }

    public override ItemEventKind Kind => ItemEventKind.Correction;

    public int CorrectsEventId { get; private set; }
    public ItemEvent? CorrectedEvent { get; private set; }

    /// <summary>The corrected field, named as the UI displays it.</summary>
    public string FieldName { get; private set; } = string.Empty;

    /// <summary>
    /// The value as originally recorded. Retained verbatim: AR 195-5 2-5b(5)'s struck-through
    /// entry must still be readable, and an auditor must always be able to see the original.
    /// </summary>
    public string? OriginalValue { get; private set; }

    public string? CorrectedValue { get; private set; }

    /// <summary>AR 195-5 1-7c(3) — the corrective action documented.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// AR 195-5 1-7c(3) — the MFR outlining the error and corrective action, filed with the
    /// proper DA Form 4137 with a copy in the case file.
    /// </summary>
    public string? MfrReference { get; private set; }

    /// <summary>AR 195-5 1-7c(3) — the supervisor informed immediately.</summary>
    public int? SupervisorNotifiedUserId { get; private set; }

    public DateTimeOffset? SupervisorNotifiedAtUtc { get; private set; }

    /// <summary>
    /// AR 195-5 1-7c(3) requires supervisor notification and an MFR when a custodian finds an
    /// incorrect entry. Whether every field-level correction in a companion system rises to that
    /// threshold is a matter of local policy, so EMC surfaces this rather than blocking: an
    /// incomplete correction is visible in the item history and to an inspector.
    /// </summary>
    public bool SatisfiesParagraph1_7c3
        => MfrReference is not null && SupervisorNotifiedUserId is not null;

    /// <summary>Links the corrected event to this correction. Called by the application service.</summary>
    public void ApplySupersession()
    {
        if (CorrectedEvent is null)
        {
            throw new DomainRuleViolationException(
                "AUD-003", "The corrected event must be loaded before supersession can be applied.");
        }

        CorrectedEvent.MarkSupersededBy(this);
    }

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
