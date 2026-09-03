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

    [Theory]
    [InlineData(AccountabilityStatus.Draft)]
    [InlineData(AccountabilityStatus.Acquired)]
    [InlineData(AccountabilityStatus.TemporaryStorage)]
    [InlineData(AccountabilityStatus.AwaitingCustodian)]
    public void PreAcceptanceStatesCannotHoldAnEvidenceRoomLocation(AccountabilityStatus status)
    {
        // AR 195-5 2-4e presupposes receipt under 2-4c. TemporaryStorage (4-3a) is the one the
        // earlier hand-written list left out.
        Assert.True(AccountabilityStateMachine.IsBeforeCustodianReceipt(status));
        Assert.False(AccountabilityStateMachine.MayHoldEvidenceRoomLocation(status));
    }

    [Theory]
    [InlineData(AccountabilityStatus.InEvidenceRoom, true)]
    [InlineData(AccountabilityStatus.TemporarilyReleased, true)]
    [InlineData(AccountabilityStatus.DiscrepancyReview, true)]
    [InlineData(AccountabilityStatus.LongTermRetention, true)]
    [InlineData(AccountabilityStatus.Disposed, false)]
    [InlineData(AccountabilityStatus.ReliefGranted, false)]
    [InlineData(AccountabilityStatus.PermanentlyTransferred, false)]
    public void ReceivedItemsMayHoldALocationUnlessTerminal(AccountabilityStatus status, bool mayHold)
    {
        Assert.True(AccountabilityStateMachine.HasBeenReceivedByCustodian(status));
        Assert.Equal(mayHold, AccountabilityStateMachine.MayHoldEvidenceRoomLocation(status));
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
