using Emc.Application.Abstractions;
using Emc.Domain.Cases;
using Emc.Domain.Events;

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

        // The item carries its own chain head (EvidenceItem.LastEventHash), so several events
        // appended in one unit of work chain to each other correctly. Sequencing and sealing
        // happen together inside AppendEvent, which is the only path that can produce a valid
        // event (invariant I-07, AUD-008).
        item.AppendEvent(itemEvent);

        _db.ItemEvents.Add(itemEvent);

        await Task.CompletedTask;
        return itemEvent;
    }
}
