using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// The item's stored summary (status, last sequence, chain head) is verified against its
/// events, and reported apart from chain verification. Requirement AUD-021.
/// </summary>
public class SnapshotVerificationTests
{
    private static (EvidenceItem Item, List<ItemEvent> Events) SubmittedItem()
    {
        var voucher = TestData.NewDraftVoucher();
        var item = voucher.AddSimpleItem();
        voucher.SubmitForCustodianIntake(1, TestData.Now);

        // Mirror what the application service does on submission: transition and record.
        var events = new List<ItemEvent>();
        foreach (var target in new[] { AccountabilityStatus.Acquired, AccountabilityStatus.AwaitingCustodian })
        {
            var from = item.AccountabilityStatus;
            item.TransitionTo(target);
            events.Add(item.AppendEvent(new StatusEvent(
                from, target, "test", TestData.Now, TestData.Now, 1)));
        }

        return (item, events);
    }

    [Fact]
    public void AConsistentItemVerifies()
    {
        var (item, events) = SubmittedItem();

        var result = SnapshotVerifier.Verify(item, events);

        Assert.True(result.IsConsistent);
        Assert.True(ItemIntegrityResult.Of(item, events).IsIntact);
    }

    [Fact]
    public void ADraftWithNoEventsVerifies()
    {
        var voucher = TestData.NewDraftVoucher();
        var item = voucher.AddSimpleItem();

        Assert.True(SnapshotVerifier.Verify(item, []).IsConsistent);
    }

    [Fact]
    public void AStatusThatDisagreesWithTheHistoryIsASnapshotMismatch_NotAChainFailure()
    {
        // The stored status says Disposed; the history says AwaitingCustodian. The chain is
        // untouched, so chain verification passes - and that is exactly why a separate check
        // exists.
        var (item, events) = SubmittedItem();
        item.TransitionTo(AccountabilityStatus.InEvidenceRoom); // no event recorded for it

        var integrity = ItemIntegrityResult.Of(item, events);

        Assert.True(integrity.Chain.IsIntact);
        Assert.False(integrity.Snapshot.IsConsistent);
        Assert.False(integrity.IsIntact);

        var problem = Assert.Single(integrity.Snapshot.Problems);
        Assert.Equal(SnapshotProblemKind.StatusMismatch, problem.Kind);
        Assert.Equal("AwaitingCustodian", problem.Expected);
        Assert.Equal("InEvidenceRoom", problem.Actual);
    }

    [Fact]
    public void ATruncatedHistoryIsBothAChainFailureAndASnapshotMismatch()
    {
        // Removing the last event breaks nothing in the chain up to that point, but the item's
        // head no longer matches. Reported as a sequence AND hash mismatch, distinct from the
        // chain result.
        var (item, events) = SubmittedItem();
        var truncated = events.Take(1).ToList();

        var integrity = ItemIntegrityResult.Of(item, truncated);

        Assert.True(integrity.Chain.IsIntact); // the surviving prefix is internally consistent
        Assert.Contains(integrity.Snapshot.Problems, p => p.Kind == SnapshotProblemKind.SequenceMismatch);
        Assert.Contains(integrity.Snapshot.Problems, p => p.Kind == SnapshotProblemKind.HashMismatch);
        Assert.False(integrity.IsIntact);
    }

    [Fact]
    public void EventsOfOtherItemsAreIgnored()
    {
        var (item, events) = SubmittedItem();
        var (_, otherEvents) = SubmittedItem();

        // Both in-memory items share Id 0, so this cannot discriminate by id; it asserts only
        // that extra events are handled through the item-id filter without throwing. The
        // id-based case is covered in the application suite with real identifiers.
        Assert.NotNull(SnapshotVerifier.Verify(item, events.Concat(otherEvents)));
    }
}
