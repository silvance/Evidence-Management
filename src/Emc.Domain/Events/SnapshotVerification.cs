using Emc.Domain.Cases;
using Emc.Domain.Common;

namespace Emc.Domain.Events;

public enum SnapshotProblemKind
{
    /// <summary>EvidenceItem.AccountabilityStatus differs from the latest StatusEvent.</summary>
    StatusMismatch = 1,

    /// <summary>EvidenceItem.LastEventSequenceNumber differs from the highest event sequence.</summary>
    SequenceMismatch = 2,

    /// <summary>EvidenceItem.LastEventHash differs from the latest event's hash.</summary>
    HashMismatch = 3
}

public sealed record SnapshotProblem(SnapshotProblemKind Kind, string Expected, string Actual, string Message);

public sealed record SnapshotVerificationResult(IReadOnlyList<SnapshotProblem> Problems)
{
    public bool IsConsistent => Problems.Count == 0;
}

/// <summary>
/// Checks an item's stored convenience fields against its append-only event history.
///
/// CONTROL. EvidenceItem carries three fields that summarize the events - AccountabilityStatus,
/// LastEventSequenceNumber, LastEventHash - because reading them is cheaper than replaying the
/// history on every list page. They are DERIVED, and anything derived can drift from its source
/// if modified out of band: a single UPDATE EvidenceItems SET AccountabilityStatus = 7 would show
/// an item as disposed while its history says it is in the evidence room, and the hash chain
/// would not notice, because the chain protects the events, not the summary.
///
/// So the summary is verified against the events, and the result is reported SEPARATELY from
/// chain verification. They mean different things:
///
///   EVENT CHAIN FAILURE      - the history itself was altered, inserted into or truncated;
///   SNAPSHOT MISMATCH        - the history is intact but the item's summary of it is wrong.
///
/// The second can be repaired by recomputing from the history. The first cannot be repaired by
/// software at all; it is an incident (AR 195-5 3-3).
///
/// Requirement AUD-021.
/// </summary>
public static class SnapshotVerifier
{
    public static SnapshotVerificationResult Verify(EvidenceItem item, IEnumerable<ItemEvent> events)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.Where(e => e.EvidenceItemId == item.Id).OrderBy(e => e.SequenceNumber).ToList();
        var problems = new List<SnapshotProblem>();

        // Status: the latest StatusEvent by append order says what the workflow last did. An
        // item with no status event was never submitted and must still be a draft.
        var expectedStatus = ordered.OfType<StatusEvent>().LastOrDefault()?.ToStatus
                             ?? AccountabilityStatus.Draft;

        if (item.AccountabilityStatus != expectedStatus)
        {
            problems.Add(new SnapshotProblem(
                SnapshotProblemKind.StatusMismatch,
                expectedStatus.ToString(), item.AccountabilityStatus.ToString(),
                $"The item is recorded as {item.AccountabilityStatus}, but its history ends in "
                + $"{expectedStatus}. The stored status was changed without a status event."));
        }

        var last = ordered.LastOrDefault();
        var expectedSequence = last?.SequenceNumber ?? 0;

        if (item.LastEventSequenceNumber != expectedSequence)
        {
            problems.Add(new SnapshotProblem(
                SnapshotProblemKind.SequenceMismatch,
                expectedSequence.ToString(), item.LastEventSequenceNumber.ToString(),
                $"The item records {item.LastEventSequenceNumber} as its last event sequence "
                + $"number, but its history reaches {expectedSequence}."));
        }

        var expectedHash = last?.EventHash;

        if (!string.Equals(item.LastEventHash, expectedHash, StringComparison.Ordinal))
        {
            problems.Add(new SnapshotProblem(
                SnapshotProblemKind.HashMismatch,
                expectedHash ?? "(none)", item.LastEventHash ?? "(none)",
                "The item's stored chain head does not match the hash of its latest event."));
        }

        return new SnapshotVerificationResult(problems);
    }
}

/// <summary>
/// Both checks on one item, kept distinct. <see cref="IsIntact"/> is true only when the history
/// verifies AND the item's summary of it agrees.
/// </summary>
public sealed record ItemIntegrityResult(
    ChainVerificationResult Chain,
    SnapshotVerificationResult Snapshot)
{
    public bool IsIntact => Chain.IsIntact && Snapshot.IsConsistent;

    public static ItemIntegrityResult Of(EvidenceItem item, IReadOnlyList<ItemEvent> events)
        => new(EventHashChain.Verify(events), SnapshotVerifier.Verify(item, events));
}
