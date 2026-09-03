using Emc.Domain.Common;

namespace Emc.Domain.Cases;

/// <summary>
/// A record that the evidence custodian assigned an AR 195-5 2-4c document number to this
/// voucher in the authoritative evidence ledger.
///
/// This is an entity rather than a column on the voucher because AR 195-5 2-7g allows a voucher
/// to carry more than one document number over its life: on permanent transfer between evidence
/// rooms the receiving custodian "will enter the next document number of the receiving evidence
/// room on both copies" and "the prior document number will be lined through in such a way that
/// it remains legible." A single column cannot represent a superseded-but-still-legible value.
///
/// Append-only (AUD-001). Requirements: VCH-005, VCH-006, VCH-008, EMC-002.
/// </summary>
public class OfficialDocumentNumberAssignment : Entity
{
    private OfficialDocumentNumberAssignment() { }

    internal OfficialDocumentNumberAssignment(
        int voucherId,
        int evidenceRoomId,
        EvidenceDocumentNumber documentNumber,
        int enteredByUserId,
        DateTimeOffset enteredAtUtc,
        bool attestedAssignedInAuthoritativeLedger,
        OfficialDocumentNumberAssignment? supersedes = null,
        string? supersessionReason = null)
    {
        ArgumentNullException.ThrowIfNull(documentNumber);

        // EMC-002 / VCH-006. In companion mode EMC does not assign the number; it records the
        // number a custodian assigned in the bound ledger (AR 195-5 2-4c, 2-5a, 2-5c). The
        // attestation is an explicit, stored assertion by the custodian, never inferred from the
        // act of typing a number.
        if (!attestedAssignedInAuthoritativeLedger)
        {
            throw new DomainRuleViolationException(
                "VCH-006",
                "AR 195-5 2-4c: the custodian must confirm that this document number was assigned "
                + "by order of precedence in the authoritative evidence ledger before it is "
                + "recorded here.");
        }

        VoucherId = voucherId;
        EvidenceRoomId = evidenceRoomId;
        DocumentNumber = documentNumber.ToString();
        Sequence = documentNumber.Sequence;
        TwoDigitYear = documentNumber.TwoDigitYear;
        CalendarYear = documentNumber.CalendarYear;
        EnteredByUserId = enteredByUserId;
        EnteredAtUtc = enteredAtUtc;
        AttestedAssignedInAuthoritativeLedger = true;

        // AR 195-5 2-7g. A BACKWARD reference: the new assignment names the one it replaces, and
        // the replaced row is never touched. The prior number stays recorded and legible, which
        // is the digital equivalent of "lined through in such a way that it remains legible".
        if (supersedes is not null)
        {
            SupersedesAssignmentId = supersedes.Id;
            SupersessionReason = Guard.NotBlank(
                supersessionReason, "VCH-008", "Supersession reason");
        }
    }

    public int VoucherId { get; private set; }
    public EvidenceVoucher? Voucher { get; private set; }

    /// <summary>
    /// AR 195-5 2-4c and 2-7g — the series runs per evidence room, per calendar year. Uniqueness
    /// is scoped to (EvidenceRoomId, CalendarYear, Sequence), never globally (invariant I-04).
    /// </summary>
    public int EvidenceRoomId { get; private set; }

    /// <summary>The number exactly as written, e.g. "037-26".</summary>
    public string DocumentNumber { get; private set; } = string.Empty;

    public int Sequence { get; private set; }
    public int TwoDigitYear { get; private set; }
    public int CalendarYear { get; private set; }

    public int EnteredByUserId { get; private set; }
    public DateTimeOffset EnteredAtUtc { get; private set; }

    /// <summary>
    /// The custodian's explicit confirmation that the number was assigned in the authoritative
    /// ledger (AR 195-5 2-4c). Stored as a first-class fact, with the attesting user and time.
    /// </summary>
    public bool AttestedAssignedInAuthoritativeLedger { get; private set; }

    /// <summary>
    /// AR 195-5 2-7g — the assignment this one replaces, named by the REPLACEMENT. Nothing is
    /// written to the replaced row, so the table needs no UPDATE path (AUD-002).
    /// </summary>
    public int? SupersedesAssignmentId { get; private set; }

    /// <summary>Why the prior number was replaced. Required when superseding.</summary>
    public string? SupersessionReason { get; private set; }

    public bool Supersedes => SupersedesAssignmentId is not null;
}
