using Emc.Domain.Common;

namespace Emc.Domain.Events;

/// <summary>
/// Legal workflow transitions for an evidence item.
///
/// This axis is deliberately separate from custody state, physical location and disposition
/// state (docs/domain-model.md §3): collapsing them into one status would lose information.
///
/// The state names derive from AR 195-5 rather than from generic evidence-software convention:
///   Acquired               2-1a, 2-3b   the agent has custody and is preparing the form
///   TemporaryStorage       4-3a         secured during non-duty hours
///   AwaitingCustodian      2-4a         due to the custodian NLT the first working day
///   InEvidenceRoom         2-4c         accepted; document number assigned
///   TemporarilyReleased    2-7a         out for lab, adjudication or disposition approval
///   DispositionPending     2-8          awaiting authority
///   Disposed               2-9          terminal
///   DiscrepancyReview      3-3a         cannot be located; 5-working-day resolution period
///   Inquiry                3-3b         official inquiry under AR 15-6
///   ReliefGranted          3-3c         terminal; permits closure of the DA Form 4137
///   LongTermRetention      2-13         sealed into a long-term container; voucher stays active
///   PermanentlyTransferred 2-7g         terminal for this room; not disposition
/// </summary>
public static class AccountabilityStateMachine
{
    private static readonly Dictionary<AccountabilityStatus, AccountabilityStatus[]> Allowed = new()
    {
        [AccountabilityStatus.Draft] =
        [
            AccountabilityStatus.Acquired
        ],

        [AccountabilityStatus.Acquired] =
        [
            AccountabilityStatus.TemporaryStorage,
            AccountabilityStatus.AwaitingCustodian,

            // AR 195-5 2-8a(1)/(2) — items with no evidentiary value, and items impractical to
            // keep, may be disposed of BEFORE being processed into the evidence room.
            AccountabilityStatus.DispositionPending
        ],

        [AccountabilityStatus.TemporaryStorage] =
        [
            AccountabilityStatus.AwaitingCustodian,
            AccountabilityStatus.DispositionPending
        ],

        [AccountabilityStatus.AwaitingCustodian] =
        [
            AccountabilityStatus.InEvidenceRoom,

            // The custodian may return a voucher to the agent to "correct and initial all errors"
            // (AR 195-5 2-3g).
            AccountabilityStatus.Acquired
        ],

        [AccountabilityStatus.InEvidenceRoom] =
        [
            AccountabilityStatus.TemporarilyReleased,
            AccountabilityStatus.DispositionPending,
            AccountabilityStatus.DiscrepancyReview,
            AccountabilityStatus.LongTermRetention,
            AccountabilityStatus.PermanentlyTransferred
        ],

        [AccountabilityStatus.TemporarilyReleased] =
        [
            AccountabilityStatus.InEvidenceRoom,
            AccountabilityStatus.DiscrepancyReview,

            // AR 195-5 2-8e(4) — evidence entered as a permanent part of the record of trial is
            // considered final disposition.
            AccountabilityStatus.DispositionPending
        ],

        [AccountabilityStatus.DispositionPending] =
        [
            AccountabilityStatus.Disposed,

            // Disposition approval can be withheld or withdrawn.
            AccountabilityStatus.InEvidenceRoom
        ],

        [AccountabilityStatus.DiscrepancyReview] =
        [
            // AR 195-5 3-3a — resolved within the 5-working-day period.
            AccountabilityStatus.InEvidenceRoom,

            // AR 195-5 3-3a — unresolved by the end of the 5th working day.
            AccountabilityStatus.Inquiry
        ],

        [AccountabilityStatus.Inquiry] =
        [
            // The item is located during the inquiry.
            AccountabilityStatus.InEvidenceRoom,

            // AR 195-5 3-3c — the inquiry fails to account for the evidence and relief is granted.
            AccountabilityStatus.ReliefGranted
        ],

        [AccountabilityStatus.LongTermRetention] =
        [
            AccountabilityStatus.InEvidenceRoom,
            AccountabilityStatus.DispositionPending,
            AccountabilityStatus.DiscrepancyReview
        ],

        // Terminal states.
        [AccountabilityStatus.Disposed] = [],
        [AccountabilityStatus.ReliefGranted] = [],
        [AccountabilityStatus.PermanentlyTransferred] = []
    };

    public static bool IsAllowed(AccountabilityStatus from, AccountabilityStatus to)
        => from != to && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<AccountabilityStatus> AllowedFrom(AccountabilityStatus from)
        => Allowed.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsTerminal(AccountabilityStatus status)
        => Allowed.TryGetValue(status, out var targets) && targets.Length == 0;
}
