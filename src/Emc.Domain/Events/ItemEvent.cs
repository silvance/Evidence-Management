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
/// inform the supervisor immediately and prepare an MFR. The single permitted mutation is
/// <see cref="SupersededByEventId"/>: null -> value, exactly once.
/// </summary>
public abstract class ItemEvent : Entity, IAppendOnly
{
    /// <summary>Version of the canonical serialization used for <see cref="EventHash"/>.</summary>
    public const int CurrentHashSchemaVersion = 1;

    protected ItemEvent() { }

    protected ItemEvent(
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? notes)
    {
        OccurredAtUtc = occurredAtLocal.ToUniversalTime();
        OccurredAtLocal = occurredAtLocal;
        OccurredAtOffset = occurredAtLocal.Offset;
        RecordedAtUtc = recordedAtUtc;
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

    /// <summary>
    /// Set when a <see cref="CorrectionEvent"/> supersedes this event. The ONLY field on an
    /// append-only record that may ever change, and only once, null -> value (invariant I-14).
    /// The superseded event is never hidden: AR 195-5 2-5b(5)'s struck-through entry must still
    /// be readable.
    /// </summary>
    public int? SupersededByEventId { get; private set; }

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

    internal void MarkSupersededBy(CorrectionEvent correction)
    {
        ArgumentNullException.ThrowIfNull(correction);

        if (SupersededByEventId is not null)
        {
            throw new AppendOnlyViolationException(
                $"Event {Id} has already been superseded by event {SupersededByEventId}. "
                + "Correct the superseding event instead.");
        }

        SupersededByEventId = correction.Id;
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

    /// <summary>One-line summary for the item history view.</summary>
    public abstract string Summarize();
}
