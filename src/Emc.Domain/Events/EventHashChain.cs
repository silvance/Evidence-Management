using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Emc.Domain.Events;

/// <summary>
/// Per-item tamper-evidence chain over accountability events.
///
/// CONTROL — not required by AR 195-5. See docs/architecture.md §4.3.
///
/// Append-only triggers stop casual modification through the application and through SSMS. They
/// do not stop someone holding ALTER TABLE. Because the application administrator must not be
/// able to rewrite evidence history (IAM-009), and because in a small-team on-premises
/// deployment the application administrator and the database administrator are frequently the
/// same person, events are chained:
///
///     EventHash = SHA-256( canonical(fields) || PreviousEventHash )
///
/// The chain is per EvidenceItem, ordered by SequenceNumber, so concurrent work on different
/// items never contends. Any altered, inserted or removed row breaks the chain from that point
/// forward, and verification is a read-only pass requiring no privileged access.
///
/// This does not make tampering impossible. It makes it DETECTABLE BY ANY READER, which is the
/// achievable and honest goal — say it that way in an inspection, not more.
/// </summary>
public static class EventHashChain
{
    /// <summary>ASCII unit separator. Cannot occur in field text, so it cannot be forged.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>ASCII record separator.</summary>
    private const char RecordSeparator = '\u001E';

    /// <summary>Stands in for a null value, so that null and empty string do not hash alike.</summary>
    private const string NullMarker = "\u0000NULL";

    /// <summary>
    /// Canonical serialization. Deliberately explicit rather than reflection- or JSON-based, so
    /// that adding a property cannot silently change the hash of existing events. Composition is
    /// versioned by <see cref="ItemEvent.CurrentHashSchemaVersion"/>.
    /// </summary>
    public static string Canonicalize(ItemEvent itemEvent)
    {
        ArgumentNullException.ThrowIfNull(itemEvent);

        var builder = new StringBuilder();
        builder.Append('v')
            .Append(itemEvent.HashSchemaVersion.ToString("D", CultureInfo.InvariantCulture))
            .Append(RecordSeparator);

        foreach (var (name, value) in itemEvent.CanonicalFields())
        {
            builder.Append(name).Append(FieldSeparator);

            // Distinguish null from empty: "" and null must not hash identically, or a value
            // could be blanked without breaking the chain.
            builder.Append(value ?? NullMarker).Append(RecordSeparator);
        }

        return builder.ToString();
    }

    public static string ComputeHash(ItemEvent itemEvent, string? previousEventHash)
    {
        ArgumentNullException.ThrowIfNull(itemEvent);

        var payload = Canonicalize(itemEvent) + "|prev:" + (previousEventHash ?? "GENESIS");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Computes and assigns the hash for a newly appended event.</summary>
    public static void Seal(ItemEvent itemEvent, string? previousEventHash)
    {
        ArgumentNullException.ThrowIfNull(itemEvent);
        itemEvent.AssignHash(previousEventHash, ComputeHash(itemEvent, previousEventHash));
    }

    /// <summary>
    /// Verifies an item's chain. <paramref name="events"/> must be every event for one item;
    /// they are ordered by sequence number here so the caller cannot get it wrong.
    /// </summary>
    public static ChainVerificationResult Verify(IEnumerable<ItemEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.OrderBy(e => e.SequenceNumber).ToList();
        var problems = new List<ChainProblem>();
        string? expectedPrevious = null;
        var expectedSequence = 1;

        foreach (var itemEvent in ordered)
        {
            // Invariant I-07 — sequence numbers are gapless. A gap means a row was removed.
            if (itemEvent.SequenceNumber != expectedSequence)
            {
                problems.Add(new ChainProblem(
                    itemEvent.Id,
                    itemEvent.SequenceNumber,
                    ChainProblemKind.SequenceGap,
                    $"Expected sequence number {expectedSequence} but found "
                    + $"{itemEvent.SequenceNumber}. An event may have been removed."));
            }

            if (itemEvent.PreviousEventHash != expectedPrevious)
            {
                problems.Add(new ChainProblem(
                    itemEvent.Id,
                    itemEvent.SequenceNumber,
                    ChainProblemKind.BrokenLink,
                    "The recorded previous-event hash does not match the preceding event. An "
                    + "event may have been inserted, removed or reordered."));
            }

            var recomputed = ComputeHash(itemEvent, itemEvent.PreviousEventHash);
            if (!string.Equals(recomputed, itemEvent.EventHash, StringComparison.Ordinal))
            {
                problems.Add(new ChainProblem(
                    itemEvent.Id,
                    itemEvent.SequenceNumber,
                    ChainProblemKind.ContentModified,
                    "The event's stored hash does not match its content. The event was modified "
                    + "after it was recorded."));
            }

            expectedPrevious = itemEvent.EventHash;
            expectedSequence = itemEvent.SequenceNumber + 1;
        }

        return new ChainVerificationResult(ordered.Count, problems);
    }
}

public enum ChainProblemKind
{
    /// <summary>An event's content no longer matches its recorded hash.</summary>
    ContentModified = 1,

    /// <summary>An event's previous-hash does not match the preceding event.</summary>
    BrokenLink = 2,

    /// <summary>A sequence number is missing, so an event was probably removed.</summary>
    SequenceGap = 3
}

public sealed record ChainProblem(int EventId, int SequenceNumber, ChainProblemKind Kind, string Message);

public sealed record ChainVerificationResult(int EventsChecked, IReadOnlyList<ChainProblem> Problems)
{
    public bool IsIntact => Problems.Count == 0;
}
