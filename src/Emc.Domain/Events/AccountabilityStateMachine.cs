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
            AccountabilityStatus.DispositionPending,

            // AR 195-5 2-3g — a line on a returned form withdrawn as entered in error. Only from
            // Acquired, which is where a returned voucher's items sit; never from any state in
            // which the custodian has received the item.
            AccountabilityStatus.WithdrawnAsEnteredInError
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
        [AccountabilityStatus.PermanentlyTransferred] = [],
        [AccountabilityStatus.WithdrawnAsEnteredInError] = []
    };

    public static bool IsAllowed(AccountabilityStatus from, AccountabilityStatus to)
        => from != to && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<AccountabilityStatus> AllowedFrom(AccountabilityStatus from)
        => Allowed.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsTerminal(AccountabilityStatus status)
        => Allowed.TryGetValue(status, out var targets) && targets.Length == 0;

    /// <summary>
    /// States an item passes through BEFORE the evidence custodian receives it under AR 195-5
    /// 2-4c: the agent has it (2-1a), it is in a temporary facility (4-3a), or it is submitted
    /// and due to the custodian (2-4a). Listed by name, deliberately. An earlier check used
    /// "status >= InEvidenceRoom", which made the meaning of a state depend on the numeric order
    /// of the enum - so inserting or reordering a member would have silently changed who counted
    /// as accepted.
    /// </summary>
    private static readonly HashSet<AccountabilityStatus> PreAcceptance =
    [
        AccountabilityStatus.Draft,
        AccountabilityStatus.Acquired,
        AccountabilityStatus.TemporaryStorage,
        AccountabilityStatus.AwaitingCustodian,
        AccountabilityStatus.WithdrawnAsEnteredInError
    ];

    /// <summary>True while the custodian has not yet received the item (AR 195-5 2-4c).</summary>
    public static bool IsBeforeCustodianReceipt(AccountabilityStatus status)
        => PreAcceptance.Contains(status);

    /// <summary>
    /// True once the custodian has received the item and assigned the document number
    /// (AR 195-5 2-4c), whatever has happened since - released, pending disposition, disposed,
    /// under inquiry, transferred. The complement of <see cref="IsBeforeCustodianReceipt"/>,
    /// and every status is exactly one or the other (a test asserts it).
    /// </summary>
    public static bool HasBeenReceivedByCustodian(AccountabilityStatus status)
        => Allowed.ContainsKey(status) && !PreAcceptance.Contains(status);

    /// <summary>
    /// States in which the item is PHYSICALLY IN THE EVIDENCE ROOM, listed by name:
    ///
    ///   InEvidenceRoom      received under 2-4c and held;
    ///   DispositionPending  approval sought (2-8) while the item is still held by the room;
    ///   LongTermRetention   sealed into a long-term container that is itself in the room (2-13).
    ///
    /// Everything else is not: before receipt the agent or a temporary facility has it (2-1a,
    /// 4-3a, 2-4a); on temporary release another party has it and the original DA Form 4137 went
    /// with it (2-7a, 2-4f(2)); in discrepancy review or inquiry it CANNOT BE LOCATED (3-3a, 3-3b),
    /// and giving it a bin would be the software resolving a discrepancy the regulation resolves
    /// through a 5-working-day review and, failing that, an AR 15-6 inquiry; and the terminal
    /// states are gone from the room.
    ///
    /// The earlier predicate was "received and not terminal", which let a released or missing
    /// item be assigned a new location.
    /// </summary>
    private static readonly HashSet<AccountabilityStatus> PhysicallyInRoom =
    [
        AccountabilityStatus.InEvidenceRoom,
        AccountabilityStatus.DispositionPending,
        AccountabilityStatus.LongTermRetention
    ];

    public static bool IsPhysicallyInEvidenceRoom(AccountabilityStatus status)
        => PhysicallyInRoom.Contains(status);

    /// <summary>
    /// True when a NEW evidence-room location may be recorded for the item. AR 195-5 2-4e concerns
    /// the location of evidence in the evidence room, so the item must be physically there. An
    /// item on temporary release keeps its last recorded location in its history - that is where
    /// it was - but cannot be given a new one until it is returned (TemporarilyReleased ->
    /// InEvidenceRoom); a missing item cannot be given one until it is found through 3-3.
    /// </summary>
    public static bool MayAssignEvidenceRoomLocation(AccountabilityStatus status)
        => IsPhysicallyInEvidenceRoom(status);

    /// <summary>Every status the machine knows. For exhaustiveness tests.</summary>
    public static IReadOnlyCollection<AccountabilityStatus> AllStatuses => Allowed.Keys;
}
