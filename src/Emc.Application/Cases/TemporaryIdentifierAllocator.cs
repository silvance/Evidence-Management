using Emc.Application.Abstractions;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Cases;

/// <summary>Allocates the EMC-only temporary identifier a draft voucher carries (VCH-003).</summary>
public interface ITemporaryIdentifierAllocator
{
    Task<TemporaryEvidenceIdentifier> AllocateAsync(int evidenceRoomId, DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// Collision-safe allocation: read the room/date counter, increment it, save. If another request
/// incremented it first the save fails on the concurrency stamp, the counter is reloaded, and
/// the increment is retried. The unique index on (EvidenceRoomId, TemporaryIdentifier) remains
/// as the backstop.
///
/// Each allocation is committed on its own before the voucher is saved. A gap in the temporary
/// series (a request that allocated and then failed) is harmless: the identifier is temporary
/// and carries no regulatory meaning. The alternative - allocating inside the voucher's own unit
/// of work - would hold the counter row until the whole request completed and serialize every
/// draft creation in the room behind it.
///
/// Requirement VCH-003, VCH-024.
/// </summary>
public sealed class TemporaryIdentifierAllocator : ITemporaryIdentifierAllocator
{
    private const int MaximumAttempts = 8;

    private readonly IEmcDbContext _db;

    public TemporaryIdentifierAllocator(IEmcDbContext db)
    {
        _db = db;
    }

    public async Task<TemporaryEvidenceIdentifier> AllocateAsync(
        int evidenceRoomId, DateOnly date, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var counter = await _db.TemporaryIdentifierCounters
                .FirstOrDefaultAsync(c => c.EvidenceRoomId == evidenceRoomId && c.Date == date, ct);

            if (counter is null)
            {
                counter = new TemporaryIdentifierCounter(evidenceRoomId, date);
                _db.TemporaryIdentifierCounters.Add(counter);
            }

            var ordinal = counter.Next();

            try
            {
                await _db.SaveChangesAsync(ct);
                return TemporaryEvidenceIdentifier.Create(date, ordinal);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Someone else took this ordinal. Reload the row as it now stands and try the
                // next one. A tracked, stale copy would otherwise keep producing the same number.
                foreach (var entry in ex.Entries)
                {
                    await entry.ReloadAsync(ct);
                }
            }
            catch (DbUpdateException) when (counter.Id == 0)
            {
                // Two requests both found no counter for this room and date and both tried to
                // create it; the unique index let one through. Forget ours and read theirs.
                _db.TemporaryIdentifierCounters.Entry(counter).State = EntityState.Detached;
            }
        }

        throw new DomainRuleViolationException(
            "VCH-024",
            $"Could not allocate a temporary identifier for evidence room {evidenceRoomId} on "
            + $"{date:yyyy-MM-dd} after {MaximumAttempts} attempts. Try again.");
    }
}
