using Emc.Application.Abstractions;
using Emc.Domain.Cases;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Items;

/// <summary>
/// Appends an event to an evidence item's history and seals it into the item's hash chain.
///
/// Centralised so that sequencing (invariant I-07) and hashing (AUD-008) cannot be forgotten:
/// no other code path may add an ItemEvent, and the EF configuration plus the SaveChanges guard
/// reject any event that arrives without a hash.
/// </summary>
public interface IItemEventRecorder
{
    Task<TEvent> AppendAsync<TEvent>(
        EvidenceItem item, TEvent itemEvent, CancellationToken cancellationToken = default)
        where TEvent : ItemEvent;
}

public sealed class ItemEventRecorder : IItemEventRecorder
{
    private readonly IEmcDbContext _db;

    public ItemEventRecorder(IEmcDbContext db) => _db = db;

    public async Task<TEvent> AppendAsync<TEvent>(
        EvidenceItem item, TEvent itemEvent, CancellationToken cancellationToken = default)
        where TEvent : ItemEvent
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(itemEvent);

        // The item's last event, which this one chains to. Read from the database rather than
        // from the in-memory collection so the chain is correct even when the item was loaded
        // without its events.
        var previousHash = await _db.ItemEvents
            .AsNoTracking()
            .Where(e => e.EvidenceItemId == item.Id)
            .OrderByDescending(e => e.SequenceNumber)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(cancellationToken);

        item.AppendEvent(itemEvent);
        EventHashChain.Seal(itemEvent, previousHash);

        _db.ItemEvents.Add(itemEvent);
        return itemEvent;
    }
}
