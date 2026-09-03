using Emc.Domain.Common;

namespace Emc.Domain.Events;

/// <summary>
/// Base type for every accountability event recorded against an evidence item.
///
/// Subtypes are separate C# classes with distinct required fields and distinct validation, but
/// they map to a single table by table-per-hierarchy (docs/architecture.md §4.1). One table
/// because "complete chronological item history" is then a single indexed query rather than a
/// five-way UNION over differently-shaped tables, and because the append-only guard, the
/// correction mechanism and the hash chain are each implemented once instead of five times.
///
/// APPEND-ONLY (AUD-001). Modelled on AR 195-5 2-5b(5) — an erroneous ledger entry is struck
/// through with one line "so it may still be read" and initialed; correction fluid, tape,
/// labels and erasures are prohibited — and 1-7c(3), which requires the discovering custodian to
/// inform the supervisor immediately and prepare an MFR.
///
/// There is NO permitted mutation. An accountability event is inserted and never updated, which
/// is why the database triggers can reject every UPDATE unconditionally rather than having to
/// decide which column change is legitimate. Corrections are separate rows that point BACKWARD at
/// what they correct (<see cref="CorrectionEvent"/>).
/// </summary>
public abstract class ItemEvent : Entity, IAppendOnly
{
    /// <summary>
    /// Fields of this event that a correction may target, mapped to the value AS ORIGINALLY
    /// RECORDED.
    ///
    /// The server derives the original value from here. It is never accepted from the client:
    /// an audit record whose "original value" came from a form post would be worthless, because
    /// the party making the correction could state whatever original they liked (AUD-014).
    ///
    /// A correction naming a field absent from this dictionary is rejected, so the correctable
    /// surface of each event type is explicit rather than open-ended.
    /// </summary>
    public abstract IReadOnlyDictionary<string, string?> CorrectableFields { get; }

    /// <summary>
    /// Identifiers behind those correctable fields that name a row rather than hold free text -
    /// an item's storage location and a change of custody's parties.
    ///
    /// A correction to one of these fields must carry the NEW identifier, not just new display
    /// text. Without it a corrected location would read "Shelf B / Bin 19" while every projection
    /// built on StorageLocationId still pointed at Bin 14, and an inventory of Bin 19 would not
    /// list the item that is actually in it.
    ///
    /// Fields absent from this dictionary are free text. Empty for most event types.
    /// </summary>
    public virtual IReadOnlyDictionary<string, EventFieldReference> ReferenceFields
        => NoReferences;

    protected static readonly IReadOnlyDictionary<string, EventFieldReference> NoReferences
        = new Dictionary<string, EventFieldReference>(StringComparer.Ordinal);

    /// <summary>Version of the canonical serialization used for <see cref="EventHash"/>.</summary>
    public const int CurrentHashSchemaVersion = 1;

    protected ItemEvent() { }

    protected ItemEvent(
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? notes)
    {
        // Normalized to whole milliseconds so the hashed value and the stored value agree on
        // any provider - see AccountabilityTime. Without this the chain would report tampering
        // whenever storage truncated sub-millisecond ticks.
        var occurred = AccountabilityTime.Normalize(occurredAtLocal);

        OccurredAtUtc = occurred.ToUniversalTime();
        OccurredAtLocal = occurred;
        OccurredAtOffset = occurred.Offset;
        RecordedAtUtc = AccountabilityTime.Normalize(recordedAtUtc);
        RecordedByUserId = recordedByUserId;
        Notes = Guard.TrimToNull(notes);
        HashSchemaVersion = CurrentHashSchemaVersion;
    }

    public abstract ItemEventKind Kind { get; }

    public int EvidenceItemId { get; private set; }

    /// <summary>
    /// Per-item monotonic sequence, gapless (invariant I-07). Per item rather than global so that
    /// concurrent work on different items never contends, and so the hash chain is per item.
    /// </summary>
    public int SequenceNumber { get; private set; }

    /// <summary>When the event actually happened, in UTC. The ordering key.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>
    /// When the event happened, in local time — what the DA Form 4137 and the ledger record
    /// ("03 SEP 26 09:15"). A UTC-only store would misrepresent the paper (AUD-011).
    /// </summary>
    public DateTimeOffset OccurredAtLocal { get; private set; }

    public TimeSpan OccurredAtOffset { get; private set; }

    /// <summary>
    /// When EMC learned of the event. Distinct from <see cref="OccurredAtUtc"/> because
    /// back-dated entry is legitimate and common — a custody transfer at 0200 recorded at 0800 —
    /// and an auditor must be able to see both (AUD-011).
    /// </summary>
    public DateTimeOffset RecordedAtUtc { get; private set; }

    public int RecordedByUserId { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>The scanned DA Form 4137 this event was imported from, if any (DOC-001).</summary>
    public int? SourceDocumentId { get; private set; }

    /// <summary>Hash of the preceding event in this item's chain; null for the first (AUD-008).</summary>
    public string? PreviousEventHash { get; private set; }

    /// <summary>SHA-256 over the canonical serialization of this event plus the previous hash.</summary>
    public string EventHash { get; private set; } = string.Empty;

    public int HashSchemaVersion { get; private set; }

    internal void AssignSequence(int evidenceItemId, int sequenceNumber)
    {
        if (SequenceNumber != 0)
        {
            throw new AppendOnlyViolationException(
                $"Event {Id} already has sequence number {SequenceNumber}.");
        }

        EvidenceItemId = evidenceItemId;
        SequenceNumber = sequenceNumber;
    }

    internal void AssignHash(string? previousEventHash, string eventHash)
    {
        if (!string.IsNullOrEmpty(EventHash))
        {
            throw new AppendOnlyViolationException($"Event {Id} has already been hashed.");
        }

        PreviousEventHash = previousEventHash;
        EventHash = Guard.NotBlank(eventHash, "AUD-008", "Event hash");
    }

    public void AttachSourceDocument(int sourceDocumentId)
    {
        if (SourceDocumentId is not null)
        {
            throw new AppendOnlyViolationException(
                $"Event {Id} is already linked to source document {SourceDocumentId}.");
        }

        SourceDocumentId = sourceDocumentId;
    }

    /// <summary>
    /// Field values contributing to this event's hash, in a stable order. Subtypes extend this.
    /// Changing the composition requires incrementing <see cref="CurrentHashSchemaVersion"/>,
    /// so existing chains stay verifiable under the version they were written with.
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        yield return new("Kind", Kind.ToString());
        yield return new("EvidenceItemId", EvidenceItemId.ToString("D", null));
        yield return new("SequenceNumber", SequenceNumber.ToString("D", null));
        yield return new("OccurredAtUtc", OccurredAtUtc.UtcDateTime.ToString("O", null));
        yield return new("OccurredAtOffset", OccurredAtOffset.ToString("c", null));
        yield return new("RecordedAtUtc", RecordedAtUtc.UtcDateTime.ToString("O", null));
        yield return new("RecordedByUserId", RecordedByUserId.ToString("D", null));
        yield return new("Notes", Notes);
        yield return new("SourceDocumentId", SourceDocumentId?.ToString("D", null));
    }

    /// <summary>
    /// True when <paramref name="fieldName"/> may be corrected on this event. Case-sensitive, so
    /// a field name is either exactly right or rejected.
    /// </summary>
    public bool IsCorrectableField(string fieldName)
        => CorrectableFields.ContainsKey(fieldName);

    /// <summary>The value as originally recorded, derived by the server (AUD-014).</summary>
    public string? OriginalValueOf(string fieldName)
        => CorrectableFields.TryGetValue(fieldName, out var value)
            ? value
            : throw new DomainRuleViolationException(
                "AUD-014",
                $"'{fieldName}' is not a correctable field on a {Kind} event. Correctable fields "
                + $"are: {string.Join(", ", CorrectableFields.Keys)}.");

    /// <summary>
    /// What kind of row a correctable field names, or <see cref="CorrectableFieldReference.None"/>
    /// for free text. Throws for a field that is not correctable at all.
    /// </summary>
    public CorrectableFieldReference ReferenceKindOf(string fieldName)
    {
        if (!IsCorrectableField(fieldName))
        {
            throw new DomainRuleViolationException(
                "AUD-014",
                $"'{fieldName}' is not a correctable field on a {Kind} event. Correctable fields "
                + $"are: {string.Join(", ", CorrectableFields.Keys)}.");
        }

        return ReferenceFields.TryGetValue(fieldName, out var reference)
            ? reference.Kind
            : CorrectableFieldReference.None;
    }

    /// <summary>The identifier as originally recorded for a reference field (AUD-014).</summary>
    public int? OriginalReferenceIdOf(string fieldName)
        => ReferenceFields.TryGetValue(fieldName, out var reference) ? reference.Id : null;

    /// <summary>One-line summary for the item history view.</summary>
    public abstract string Summarize();
}
