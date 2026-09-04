using Emc.Application.Abstractions;
using Emc.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Emc.Application.Documents;

public sealed record OrphanSweepReport(int Enumerated, int Referenced, int StagedLeft, int PartialsRemoved, int OrphansRemoved);

/// <summary>
/// Reconciles the blob store against the database (OCR-018 / DOC-006). Three states, three
/// outcomes:
///
///   COMMITTED and REFERENCED - a SourceDocument, DocumentRenderPage or OcrRunPage names the key:
///     never touched, whatever its age;
///   COMMITTED and UNREFERENCED, younger than the grace window - STAGED: a write whose record is
///     still being saved, or an attempt about to be unwound by its own processor. Left alone;
///   COMMITTED and UNREFERENCED, older than the grace window - ORPHAN: a crash between the blob
///     write and the record write. Removed;
///   PARTIAL (a ".partial" left by an interrupted write) older than the grace window - removed.
///
/// The referenced set is read from the database in the same pass as the store listing, and a
/// key is deleted only when the database does not name it: a referenced blob is never deleted,
/// which is the invariant that matters. Counts only are logged.
/// </summary>
public interface IOrphanBlobSweeper
{
    Task<OrphanSweepReport> SweepAsync(TimeSpan grace, CancellationToken ct = default);
}

public sealed class OrphanBlobSweeper : IOrphanBlobSweeper
{
    private readonly IEmcDbContext _db;
    private readonly ISourceDocumentStore _store;
    private readonly IClock _clock;
    private readonly ILogger<OrphanBlobSweeper> _logger;

    public OrphanBlobSweeper(IEmcDbContext db, ISourceDocumentStore store, IClock clock, ILogger<OrphanBlobSweeper> logger)
    {
        _db = db;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    public async Task<OrphanSweepReport> SweepAsync(TimeSpan grace, CancellationToken ct = default)
    {
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(grace), "The grace window cannot be negative.");
        }

        var entries = await _store.EnumerateAsync(ct);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        referenced.UnionWith(await _db.SourceDocuments.AsNoTracking().Select(d => d.StorageKey).ToListAsync(ct));
        referenced.UnionWith(await _db.DocumentRenderPages.AsNoTracking().Select(p => p.StorageKey).ToListAsync(ct));
        referenced.UnionWith(await _db.OcrRuns.AsNoTracking().SelectMany(r => r.Pages).Select(p => p.StorageKey).ToListAsync(ct));

        var cutoff = _clock.UtcNow - grace;
        int referencedCount = 0, staged = 0, partials = 0, orphans = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.State == StoredBlobState.Partial)
            {
                if (entry.LastWriteUtc <= cutoff && await _store.TryDeletePartialAsync(entry.StorageKey, ct))
                {
                    partials++;
                }

                continue;
            }

            if (referenced.Contains(entry.StorageKey))
            {
                referencedCount++;
                continue;
            }

            if (entry.LastWriteUtc > cutoff)
            {
                staged++;
                continue;
            }

            if (await _store.TryDeleteAsync(entry.StorageKey, ct))
            {
                orphans++;
            }
        }

        var report = new OrphanSweepReport(entries.Count, referencedCount, staged, partials, orphans);
        _logger.LogInformation("Blob store reconciled: {Enumerated} entries, {Referenced} referenced, {Staged} staged (left), {Partials} partial(s) removed, {Orphans} orphan(s) removed.",
            report.Enumerated, report.Referenced, report.StagedLeft, report.PartialsRemoved, report.OrphansRemoved);
        return report;
    }
}
