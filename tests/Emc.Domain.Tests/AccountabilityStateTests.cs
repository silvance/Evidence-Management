using Emc.Domain.Common;
using Emc.Domain.Events;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// Workflow state transitions, derived from AR 195-5 rather than generic evidence-software
/// convention. Requirements: ITEM-001, LOSS-005, DISP-011.
/// </summary>
public class AccountabilityStateTests
{
    [Theory]
    [InlineData(AccountabilityStatus.Draft, AccountabilityStatus.Acquired)]
    [InlineData(AccountabilityStatus.Acquired, AccountabilityStatus.AwaitingCustodian)]
    [InlineData(AccountabilityStatus.Acquired, AccountabilityStatus.TemporaryStorage)]
    [InlineData(AccountabilityStatus.AwaitingCustodian, AccountabilityStatus.InEvidenceRoom)]
    [InlineData(AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.TemporarilyReleased)]
    [InlineData(AccountabilityStatus.TemporarilyReleased, AccountabilityStatus.InEvidenceRoom)]
    [InlineData(AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.DiscrepancyReview)]
    [InlineData(AccountabilityStatus.DiscrepancyReview, AccountabilityStatus.Inquiry)]
    [InlineData(AccountabilityStatus.Inquiry, AccountabilityStatus.ReliefGranted)]
    [InlineData(AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.LongTermRetention)]
    [InlineData(AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.PermanentlyTransferred)]
    public void PermittedTransitions(AccountabilityStatus from, AccountabilityStatus to)
        => Assert.True(AccountabilityStateMachine.IsAllowed(from, to));

    [Theory]
    // AR 195-5 2-4c - evidence cannot enter the evidence room without passing through the
    // custodian, so Draft cannot jump to InEvidenceRoom.
    [InlineData(AccountabilityStatus.Draft, AccountabilityStatus.InEvidenceRoom)]
    // Terminal states are terminal.
    [InlineData(AccountabilityStatus.Disposed, AccountabilityStatus.InEvidenceRoom)]
    [InlineData(AccountabilityStatus.ReliefGranted, AccountabilityStatus.InEvidenceRoom)]
    [InlineData(AccountabilityStatus.PermanentlyTransferred, AccountabilityStatus.InEvidenceRoom)]
    // Relief for accountability is the outcome of an inquiry (3-3c), not of an inventory finding.
    [InlineData(AccountabilityStatus.DiscrepancyReview, AccountabilityStatus.ReliefGranted)]
    // Disposal requires the disposition workflow (2-8), never a jump.
    [InlineData(AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.Disposed)]
    public void ForbiddenTransitions(AccountabilityStatus from, AccountabilityStatus to)
        => Assert.False(AccountabilityStateMachine.IsAllowed(from, to));

    [Fact]
    public void PreEvidenceRoomDisposalIsPermitted()
    {
        // AR 195-5 2-8a(1) and 2-8a(2): items with no evidentiary value, and items impractical to
        // keep (vehicles, perishables, large amounts of money), may be disposed of BEFORE being
        // released to the evidence custodian.
        Assert.True(AccountabilityStateMachine.IsAllowed(
            AccountabilityStatus.Acquired, AccountabilityStatus.DispositionPending));
    }

    [Fact]
    public void ACustodianMayReturnAVoucherToTheAgentForCorrection()
    {
        // AR 195-5 2-3g: "Evidence custodians will review the DA Form 4137 submitted with
        // evidence and have the submitting DALEO or Army CI agent correct and initial all errors."
        Assert.True(AccountabilityStateMachine.IsAllowed(
            AccountabilityStatus.AwaitingCustodian, AccountabilityStatus.Acquired));
    }

    [Fact]
    public void EveryStatusIsEitherBeforeOrAfterCustodianReceipt_NeverBothOrNeither()
    {
        // The exhaustiveness guard for the semantic predicates. A status added to the enum and
        // the machine without being classified fails here, instead of silently counting as
        // "accepted" because of where it landed in the numeric order.
        var all = Enum.GetValues<AccountabilityStatus>();

        Assert.Equal(all.Length, AccountabilityStateMachine.AllStatuses.Count);

        foreach (var status in all)
        {
            var before = AccountabilityStateMachine.IsBeforeCustodianReceipt(status);
            var after = AccountabilityStateMachine.HasBeenReceivedByCustodian(status);

            Assert.True(before ^ after, $"{status} must be exactly one of before/after receipt.");
        }
    }

    /// <summary>
    /// LOC-008. Every status, classified by physical presence. AR 195-5 2-4e concerns the
    /// location of evidence IN the evidence room. The earlier predicate ("received and not
    /// terminal") let an item on temporary release - in another party's custody, its original
    /// DA Form 4137 with it (2-7a, 2-4f(2)) - and an item that cannot be located (3-3) be given a
    /// new bin. Exhaustive: a status added to the enum without a row here fails the count test.
    /// </summary>
    public static IEnumerable<object[]> PresenceByStatus() =>
    [
        [AccountabilityStatus.Draft, false],
        [AccountabilityStatus.Acquired, false],
        [AccountabilityStatus.TemporaryStorage, false],          // 4-3a temporary facility
        [AccountabilityStatus.AwaitingCustodian, false],
        [AccountabilityStatus.InEvidenceRoom, true],
        [AccountabilityStatus.TemporarilyReleased, false],       // 2-7a: out, with the original form
        [AccountabilityStatus.DispositionPending, true],         // 2-8: still held while approval is sought
        [AccountabilityStatus.Disposed, false],
        [AccountabilityStatus.DiscrepancyReview, false],         // 3-3a: cannot be located
        [AccountabilityStatus.Inquiry, false],                   // 3-3b: cannot be located
        [AccountabilityStatus.ReliefGranted, false],
        [AccountabilityStatus.LongTermRetention, true],          // 2-13: sealed container, in the room
        [AccountabilityStatus.PermanentlyTransferred, false],
        [AccountabilityStatus.WithdrawnAsEnteredInError, false]  // never a physical item
    ];

    [Theory]
    [MemberData(nameof(PresenceByStatus))]
    public void ANewLocationMayBeAssignedOnlyToAnItemPhysicallyInTheRoom(AccountabilityStatus status, bool inRoom)
    {
        Assert.Equal(inRoom, AccountabilityStateMachine.IsPhysicallyInEvidenceRoom(status));
        Assert.Equal(inRoom, AccountabilityStateMachine.MayAssignEvidenceRoomLocation(status));
    }

    [Fact]
    public void ThePresenceTableCoversEveryStatus()
    {
        var classified = PresenceByStatus().Select(r => (AccountabilityStatus)r[0]).ToHashSet();
        Assert.Equal(Enum.GetValues<AccountabilityStatus>().ToHashSet(), classified);
    }

    [Fact]
    public void AReleasedItemRegainsTheLocationWorkflowOnlyByReturningToTheRoom()
    {
        // The historical rule is preserved: the last location stays in history. A NEW one needs
        // the state transition back into the room first.
        Assert.False(AccountabilityStateMachine.MayAssignEvidenceRoomLocation(AccountabilityStatus.TemporarilyReleased));
        Assert.True(AccountabilityStateMachine.IsAllowed(AccountabilityStatus.TemporarilyReleased, AccountabilityStatus.InEvidenceRoom));
        Assert.True(AccountabilityStateMachine.MayAssignEvidenceRoomLocation(AccountabilityStatus.InEvidenceRoom));

        // A missing item is found through 3-3, not by being given a bin.
        Assert.False(AccountabilityStateMachine.MayAssignEvidenceRoomLocation(AccountabilityStatus.DiscrepancyReview));
        Assert.False(AccountabilityStateMachine.MayAssignEvidenceRoomLocation(AccountabilityStatus.Inquiry));
        Assert.True(AccountabilityStateMachine.IsAllowed(AccountabilityStatus.Inquiry, AccountabilityStatus.InEvidenceRoom));
    }

    [Fact]
    public void AWithdrawnLineIsTerminalAndReachableOnlyFromAcquired()
    {
        // VCH-026. Only a line on a returned form (whose items sit in Acquired) can be withdrawn;
        // nothing the custodian has received can be.
        Assert.True(AccountabilityStateMachine.IsTerminal(AccountabilityStatus.WithdrawnAsEnteredInError));
        Assert.True(AccountabilityStateMachine.IsBeforeCustodianReceipt(AccountabilityStatus.WithdrawnAsEnteredInError));

        foreach (var from in Enum.GetValues<AccountabilityStatus>())
        {
            Assert.Equal(
                from == AccountabilityStatus.Acquired,
                AccountabilityStateMachine.IsAllowed(from, AccountabilityStatus.WithdrawnAsEnteredInError));
        }
    }

    [Fact]
    public void PredicatesDoNotDependOnEnumOrder()
    {
        // The specific regression. DiscrepancyReview (8) and Inquiry (9) are numerically ABOVE
        // Disposed (7) and below nothing meaningful; LongTermRetention sits after ReliefGranted.
        // None of that is what decides receipt.
        Assert.True(AccountabilityStateMachine.HasBeenReceivedByCustodian(AccountabilityStatus.Inquiry));
        Assert.True(AccountabilityStateMachine.HasBeenReceivedByCustodian(AccountabilityStatus.LongTermRetention));
        Assert.False(AccountabilityStateMachine.HasBeenReceivedByCustodian(AccountabilityStatus.TemporaryStorage));
    }

    [Fact]
    public void TemporaryReleaseIsAnAccountabilityState_NotAStorageLocationKind()
    {
        // LOC-005. AR 195-5 2-7a/2-7b: a temporary release is evidence being OUT of the room in
        // someone's custody, not a place in it. The model has the state and no such location.
        Assert.True(Enum.IsDefined(AccountabilityStatus.TemporarilyReleased));
        Assert.DoesNotContain(
            Enum.GetNames<Emc.Domain.Common.StorageLocationKind>(),
            n => n.Contains("Release", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReliefGrantedIsTerminal()
    {
        // AR 195-5 3-3c - relief "permits the closure of the DA Form 4137" (LOSS-005).
        Assert.True(AccountabilityStateMachine.IsTerminal(AccountabilityStatus.ReliefGranted));
        Assert.Empty(AccountabilityStateMachine.AllowedFrom(AccountabilityStatus.ReliefGranted));
    }

    [Fact]
    public void PermanentTransferIsTerminalForThisRoomButIsNotDisposition()
    {
        // AR 195-5 2-7g - the receiving evidence room assigns its own next document number. The
        // evidence still exists; this room's accountability for it has ended (DISP-011).
        Assert.True(AccountabilityStateMachine.IsTerminal(AccountabilityStatus.PermanentlyTransferred));
        Assert.NotEqual(AccountabilityStatus.Disposed, AccountabilityStatus.PermanentlyTransferred);
    }

    [Fact]
    public void ATransitionToItselfIsNotATransition()
        => Assert.False(AccountabilityStateMachine.IsAllowed(
            AccountabilityStatus.InEvidenceRoom, AccountabilityStatus.InEvidenceRoom));
}
