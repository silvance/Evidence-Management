using Emc.Domain.Common;

namespace Emc.Domain.Cases;

/// <summary>
/// The last temporary-identifier ordinal issued for an evidence room on a given date.
///
/// Exists so allocation is a database-backed increment guarded by optimistic concurrency, rather
/// than COUNT(*) + 1. Two draft vouchers created at the same moment both computed the same count
/// and collided on the unique index; and COUNT was wrong anyway once any draft had been deleted.
///
/// The identifier this feeds is NOT a regulatory number (VCH-003). Nothing here touches the
/// AR 195-5 2-4c document number, which the custodian assigns from the ledger.
/// </summary>
public class TemporaryIdentifierCounter : Entity, IConcurrencyStamped
{
    private TemporaryIdentifierCounter() { }

    public TemporaryIdentifierCounter(int evidenceRoomId, DateOnly date)
    {
        EvidenceRoomId = evidenceRoomId;
        Date = date;
        LastOrdinal = 0;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int EvidenceRoomId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>The last ordinal handed out. Never decreases.</summary>
    public int LastOrdinal { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>Claims the next ordinal. Committed only if no one else has claimed one since this row was read.</summary>
    public int Next() => ++LastOrdinal;
}
