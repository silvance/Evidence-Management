namespace Emc.Domain.Events;

/// <summary>
/// An event as it now reads, after any field-level corrections are applied.
///
/// This is what current-state projections must use. AR 195-5 2-5b(5) keeps a corrected entry
/// readable but the corrected value is what the record now says, so after correcting an item's
/// location from "Shelf B / Bin 14" to "Shelf B / Bin 19" the CURRENT LOCATION is Bin 19 - not
/// Bin 14, and not nothing.
///
/// The earlier model excluded corrected events from projections entirely, which meant correcting
/// the only location event left the item with no recorded location at all. That is the defect
/// this type exists to prevent.
///
/// The original is never discarded: <see cref="Original"/> and <see cref="Corrections"/> are both
/// carried, and the history view shows both.
/// </summary>
public sealed class EffectiveItemEvent
{
    private readonly Dictionary<string, CorrectionEvent> _latestCorrectionByField;

    public EffectiveItemEvent(ItemEvent original, IEnumerable<CorrectionEvent> corrections)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(corrections);

        Original = original;

        Corrections = corrections
            .Where(c => c.CorrectsEventId == original.Id)
            .OrderBy(c => c.OccurredAtUtc)
            .ThenBy(c => c.SequenceNumber)
            .ToList();

        // A field may be corrected more than once; the most recent correction wins. Ordering is
        // by occurrence then sequence, so a back-dated correction cannot silently take precedence
        // over a later one recorded first.
        _latestCorrectionByField = Corrections
            .GroupBy(c => c.FieldName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }

    public ItemEvent Original { get; }

    /// <summary>Every correction against this event, in order. Never hidden.</summary>
    public IReadOnlyList<CorrectionEvent> Corrections { get; }

    public bool HasCorrections => Corrections.Count > 0;

    /// <summary>Fields of this event that have been corrected.</summary>
    public IReadOnlyCollection<string> CorrectedFieldNames => _latestCorrectionByField.Keys;

    public bool IsCorrected(string fieldName) => _latestCorrectionByField.ContainsKey(fieldName);

    /// <summary>
    /// The field's value as the record now reads: the most recent correction if there is one,
    /// otherwise the value as originally recorded. Fields never corrected keep their original
    /// values, so correcting one field does not disturb the others.
    /// </summary>
    public string? EffectiveValueOf(string fieldName)
        => _latestCorrectionByField.TryGetValue(fieldName, out var correction)
            ? correction.CorrectedValue
            : Original.OriginalValueOf(fieldName);

    /// <summary>The correction that produced the current value of a field, if any.</summary>
    public CorrectionEvent? CorrectionFor(string fieldName)
        => _latestCorrectionByField.GetValueOrDefault(fieldName);

    /// <summary>Every correctable field, with its effective value.</summary>
    public IReadOnlyDictionary<string, string?> EffectiveFields
        => Original.CorrectableFields.Keys
            .ToDictionary(f => f, EffectiveValueOf, StringComparer.Ordinal);

    /// <summary>
    /// The IDENTIFIER a field now names, for fields that name a row: the most recent correction's
    /// replacement row if there is one, otherwise the row recorded originally. Null for free text.
    ///
    /// This is the half that the earlier text-only correction model lost. Correcting a location
    /// from Bin 14 to Bin 19 changed the displayed path while every projection built on
    /// StorageLocationId still pointed at Bin 14, so an inventory of Bin 19 would not have listed
    /// the item that the record said was in it (AUD-016).
    /// </summary>
    public int? EffectiveReferenceIdOf(string fieldName)
        => _latestCorrectionByField.TryGetValue(fieldName, out var correction)
           && correction.IsReferenceCorrection
            ? correction.CorrectedReferenceId
            : Original.OriginalReferenceIdOf(fieldName);

    /// <summary>
    /// The storage location this event now names, for a <see cref="LocationEvent"/>. Null for any
    /// other event type.
    /// </summary>
    public int? EffectiveStorageLocationId
        => Original is LocationEvent
            ? EffectiveReferenceIdOf(nameof(LocationEvent.StorageLocationPath))
            : null;

    /// <summary>The receiving custody party this event now names, for a <see cref="CustodyEvent"/>.</summary>
    public int? EffectiveReceivedByPartyId
        => Original is CustodyEvent
            ? EffectiveReferenceIdOf(nameof(CustodyEvent.ReceivedBy))
            : null;

    /// <summary>The releasing custody party this event now names, for a <see cref="CustodyEvent"/>.</summary>
    public int? EffectiveReleasedByPartyId
        => Original is CustodyEvent
            ? EffectiveReferenceIdOf(nameof(CustodyEvent.ReleasedBy))
            : null;
}

/// <summary>Builds effective views over an item's event history.</summary>
public static class EffectiveHistory
{
    /// <summary>
    /// Projects every non-correction event with its corrections applied, in chronological order.
    /// Correction events themselves stay in the raw history for display but are not projected as
    /// events in their own right - a correction is not a custody transfer or a location change.
    /// </summary>
    public static IReadOnlyList<EffectiveItemEvent> Project(IEnumerable<ItemEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var all = events.ToList();
        var corrections = all.OfType<CorrectionEvent>().ToList();

        return all
            .Where(e => e is not CorrectionEvent)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.SequenceNumber)
            .Select(e => new EffectiveItemEvent(e, corrections))
            .ToList();
    }

    /// <summary>
    /// The most recent event of a given type, with corrections applied. Used for the current
    /// location and current custody projections.
    /// </summary>
    public static EffectiveItemEvent? LatestOf<TEvent>(IEnumerable<ItemEvent> events)
        where TEvent : ItemEvent
    {
        ArgumentNullException.ThrowIfNull(events);

        var all = events.ToList();
        var corrections = all.OfType<CorrectionEvent>().ToList();

        var latest = all
            .OfType<TEvent>()
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.SequenceNumber)
            .FirstOrDefault();

        return latest is null ? null : new EffectiveItemEvent(latest, corrections);
    }
}
