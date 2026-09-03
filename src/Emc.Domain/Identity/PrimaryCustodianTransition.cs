using Emc.Domain.Common;

namespace Emc.Domain.Identity;

/// <summary>
/// A change of primary evidence custodian, and the joint inventory AR 195-5 requires with it.
///
/// This is the path AR 195-5 para 3-2d prescribes when the alternate cannot simply act under the
/// temporary-absence provision:
///
///   3-2d: "When the primary evidence custodian changes, the incoming and outgoing primary
///   custodians will conduct a joint physical inventory of all evidence in the evidence room ...
///   The outgoing custodian will resolve all discrepancies, before transfer of accountability ...
///   if it is known that the primary custodian will be gone for more than 30 consecutive calendar
///   days, the alternate will be appointed on orders as the primary custodian, and a joint
///   inventory will be conducted."
///
///   3-2g(3): the prescribed ledger statement, signed by BOTH incoming and outgoing primary
///   custodians, including "Any discrepancies have been resolved to my satisfaction."
///
/// EMC represents the transition and whether its required inventory is still pending. It does NOT
/// implement the inventory subsystem - that is a later slice - but the transition cannot be
/// declared complete without one, so a half-finished handover is visible rather than assumed.
///
/// Note what this does and does not gate. Authority to act as primary custodian comes from the
/// APPOINTMENT ON ORDERS (1-4g(1)), which is why the incoming custodian can work as soon as the
/// appointment is effective. The joint inventory governs TRANSFER OF ACCOUNTABILITY, and 3-2g(3)
/// has the incoming custodian attest to it in the ledger afterwards. Conflating the two would
/// either block a lawfully appointed custodian or pretend an unfinished handover was finished.
///
/// Requirements: IAM-021, IAM-022.
/// </summary>
public class PrimaryCustodianTransition : Entity, IConcurrencyStamped
{
    private PrimaryCustodianTransition() { }

    public PrimaryCustodianTransition(
        int evidenceRoomId,
        int incomingPrimaryAppointmentId,
        int? outgoingPrimaryAppointmentId,
        PrimaryCustodianTransitionReason reason,
        DateTimeOffset effectiveFrom,
        int recordedByUserId,
        DateTimeOffset recordedAtUtc,
        string? notes = null)
    {
        EvidenceRoomId = evidenceRoomId;
        IncomingPrimaryAppointmentId = incomingPrimaryAppointmentId;
        OutgoingPrimaryAppointmentId = outgoingPrimaryAppointmentId;
        Reason = reason;
        EffectiveFrom = effectiveFrom;
        RecordedByUserId = recordedByUserId;
        RecordedAtUtc = recordedAtUtc;
        Notes = Guard.TrimToNull(notes);
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int EvidenceRoomId { get; private set; }

    public int IncomingPrimaryAppointmentId { get; private set; }

    /// <summary>
    /// Null where there is no outgoing custodian able to participate. AR 195-5 3-2g(5) covers the
    /// death or incapacity case, where the Released By block reads "N/A Custodian Unable to Sign".
    /// </summary>
    public int? OutgoingPrimaryAppointmentId { get; private set; }

    public PrimaryCustodianTransitionReason Reason { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }

    /// <summary>
    /// AR 195-5 3-2d - a joint physical inventory of all evidence in the evidence room. Always
    /// required on a change of primary custodian; recorded here as pending until completed.
    /// </summary>
    public DateTimeOffset? JointInventoryCompletedAt { get; private set; }

    /// <summary>Reference to the inventory record or its paper documentation.</summary>
    public string? JointInventoryReference { get; private set; }

    /// <summary>
    /// AR 195-5 3-2d - "The outgoing custodian will resolve all discrepancies, before transfer of
    /// accountability", and 3-2g(3)'s ledger statement attests they were resolved.
    /// </summary>
    public bool DiscrepanciesResolved { get; private set; }

    /// <summary>
    /// AR 195-5 3-2g(3) - the handwritten statement signed by both incoming and outgoing primary
    /// custodians in the evidence ledger. EMC records that the paper entry was made (AUD-013).
    /// </summary>
    public string? LedgerAttestation { get; private set; }

    public int RecordedByUserId { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public string? Notes { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>
    /// True only when the joint inventory is done, discrepancies are resolved, and the ledger
    /// statement has been recorded. Until then the handover is visibly unfinished.
    /// </summary>
    public bool IsComplete
        => JointInventoryCompletedAt is not null
           && DiscrepanciesResolved
           && !string.IsNullOrWhiteSpace(LedgerAttestation);

    /// <summary>AR 195-5 3-2d and 3-2g(3) - record the joint inventory and the ledger statement.</summary>
    public void RecordJointInventory(
        DateTimeOffset completedAt,
        string jointInventoryReference,
        bool discrepanciesResolved,
        string ledgerAttestation)
    {
        if (JointInventoryCompletedAt is not null)
        {
            throw new DomainRuleViolationException(
                "IAM-022", "The joint inventory for this transition has already been recorded.");
        }

        if (completedAt < EffectiveFrom)
        {
            throw new DomainRuleViolationException(
                "IAM-022",
                "The joint inventory cannot be completed before the transition takes effect.");
        }

        // AR 195-5 3-2d - the outgoing custodian resolves all discrepancies BEFORE transfer of
        // accountability, and 3-2g(3)'s statement attests to it. An unresolved-discrepancy
        // handover is recorded as incomplete rather than silently accepted.
        if (!discrepanciesResolved)
        {
            throw new DomainRuleViolationException(
                "IAM-022",
                "AR 195-5 para 3-2d: the outgoing custodian resolves all discrepancies before "
                + "transfer of accountability. Record the discrepancies and resolve them before "
                + "completing the transition.");
        }

        JointInventoryCompletedAt = completedAt;
        JointInventoryReference = Guard.NotBlank(
            jointInventoryReference, "IAM-022", "Joint inventory reference");
        DiscrepanciesResolved = true;
        LedgerAttestation = Guard.NotBlank(ledgerAttestation, "IAM-022", "Ledger attestation");
    }
}

/// <summary>Why the primary evidence custodian changed.</summary>
public enum PrimaryCustodianTransitionReason
{
    /// <summary>An ordinary change of primary custodian (AR 195-5 3-2a(2), 3-2d).</summary>
    ChangeOfCustodian = 1,

    /// <summary>
    /// AR 195-5 3-2d - the primary's absence is known to exceed 30 consecutive days, so the
    /// alternate is appointed primary on orders rather than acting under 1-4i.
    /// </summary>
    KnownLongAbsence = 2,

    /// <summary>
    /// AR 195-5 3-2d / 3-2g(5) - death, sudden illness, an absence that extended beyond 30 days,
    /// or emergency transfer, where the outgoing custodian cannot participate or sign.
    /// </summary>
    IncapacityOrEmergency = 3
}
