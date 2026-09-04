using Emc.Application.Abstractions;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emc.Application.Documents;

/// <summary>
/// The worker's render loop body (DOC-014, DOC-015): lease one render job, read the immutable
/// original from the store, count and measure pages against the limits, render each page
/// through the rasterizer - in the worker an isolated child process with a hard timeout - store
/// the images, write ONE immutable run, settle the job. Never runs in the web process.
///
/// Blob discipline: page images are written to the store before the run row exists; if the
/// run cannot be saved the written blobs are removed. Logging carries ids, counts, durations and
/// categories only.
/// </summary>
public interface IDocumentRenderProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken ct = default);
}

public sealed class DocumentRenderProcessor : IDocumentRenderProcessor
{
    private readonly IEmcDbContext _db;
    private readonly ISourceDocumentStore _store;
    private readonly IPdfRasterizer _rasterizer;
    private readonly IClock _clock;
    private readonly SourceDocumentOptions _options;
    private readonly ILogger<DocumentRenderProcessor> _logger;
    private readonly string _workerId;

    public DocumentRenderProcessor(
        IEmcDbContext db, ISourceDocumentStore store, IPdfRasterizer rasterizer, IClock clock,
        IOptions<SourceDocumentOptions> options, IOptions<Emc.Application.Ocr.OcrOptions> workerOptions, ILogger<DocumentRenderProcessor> logger)
    {
        _db = db;
        _store = store;
        _rasterizer = rasterizer;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        var workerId = workerOptions.Value.WorkerId;
        _workerId = string.IsNullOrWhiteSpace(workerId) ? $"{Environment.MachineName}/{Environment.ProcessId}" : workerId.Trim();
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        var job = await LeaseNextAsync(ct);
        if (job is null)
        {
            return false;
        }

        var started = _clock.UtcNow;
        _logger.LogInformation("Render job {JobId} leased by {WorkerId} for document {DocumentId} (attempt {Attempt}).", job.Id, _workerId, job.SourceDocumentId, job.Attempts);

        var written = new List<string>();
        DocumentRenderRun run;
        try
        {
            using var jobTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            jobTimeout.CancelAfter(TimeSpan.FromSeconds(_options.RenderTimeoutSeconds * Math.Max(1, _options.MaxPageCount / 10 + 1)));
            run = await ExecuteAsync(job, started, written, jobTimeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await UnwindAsync(written);
            _logger.LogWarning("Render job {JobId}: worker stopping; lease left to expire.", job.Id);
            throw;
        }
        catch (OperationCanceledException)
        {
            await UnwindAsync(written);
            run = FailedRun(job, started, RenderFailureCategory.Timeout);
        }
        catch (MalformedPdfException)
        {
            await UnwindAsync(written);
            run = FailedRun(job, started, RenderFailureCategory.MalformedPdf);
        }
        catch (RendererCrashedException)
        {
            await UnwindAsync(written);
            run = FailedRun(job, started, RenderFailureCategory.RendererCrashed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await UnwindAsync(written);
            _logger.LogError("Render job {JobId}: unexpected {ExceptionType}.", job.Id, ex.GetType().Name);
            run = FailedRun(job, started, RenderFailureCategory.Unexpected);
        }

        _db.DocumentRenderRuns.Add(run);
        if (run.Outcome == RenderRunOutcome.Succeeded)
        {
            job.Complete(_workerId, _clock.UtcNow);
        }
        else
        {
            job.Fail(_workerId, _clock.UtcNow, run.FailureCategory, _options.MaxRenderAttempts);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // The run could not be persisted: its page blobs would be orphans. Remove them.
            await UnwindAsync(written);
            throw;
        }

        _logger.LogInformation("Render job {JobId}: {Outcome} ({Category}); {Pages} page(s) in {Ms} ms; job now {Status}.",
            job.Id, run.Outcome, run.FailureCategory, run.Pages.Count, (int)(run.CompletedAtUtc - run.StartedAtUtc).TotalMilliseconds, job.Status);
        return true;
    }

    private async Task<DocumentRenderJob?> LeaseNextAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var candidates = await _db.DocumentRenderJobs
            .Where(j => j.Status == RenderJobStatus.Queued || (j.Status == RenderJobStatus.Running && j.LeaseExpiresUtc != null && j.LeaseExpiresUtc <= now))
            .OrderBy(j => j.RequestedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        foreach (var job in candidates)
        {
            try
            {
                job.Lease(_workerId, now, TimeSpan.FromSeconds(_options.RenderLeaseSeconds), _options.MaxRenderAttempts);
                await _db.SaveChangesAsync(ct);
                return job;
            }
            catch (DomainRuleViolationException)
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                foreach (var entry in ((DbContext)_db).ChangeTracker.Entries<DocumentRenderJob>().Where(e => e.Entity.Id == job.Id).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        return null;
    }

    private async Task<DocumentRenderRun> ExecuteAsync(DocumentRenderJob job, DateTimeOffset started, List<string> written, CancellationToken ct)
    {
        var document = await _db.SourceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == job.SourceDocumentId, ct);
        if (document is null)
        {
            return FailedRun(job, started, RenderFailureCategory.DocumentUnavailable);
        }

        byte[] pdf;
        await using (var stream = await _store.OpenReadAsync(document.StorageKey, ct))
        {
            if (stream is null)
            {
                return FailedRun(job, started, RenderFailureCategory.DocumentUnavailable);
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            pdf = buffer.ToArray();
        }

        // The bytes must still be the bytes received (AUD-022) before a parser sees them.
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf)).ToLowerInvariant(), document.Sha256, StringComparison.Ordinal))
        {
            return FailedRun(job, started, RenderFailureCategory.DocumentUnavailable);
        }

        // Page count and dimensions against the limits, before any page is rendered.
        var pageCount = _rasterizer.GetPageCount(pdf);
        if (pageCount < 1 || pageCount > _options.MaxPageCount)
        {
            return FailedRun(job, started, RenderFailureCategory.ResourceLimitExceeded, pageCount);
        }

        foreach (var page in _rasterizer.GetPageDimensions(pdf))
        {
            var pixels = (long)Math.Ceiling(page.WidthPoints / 72.0 * _options.RenderDpi) * (long)Math.Ceiling(page.HeightPoints / 72.0 * _options.RenderDpi);
            if (page.WidthPoints <= 0 || page.HeightPoints <= 0 || pixels > _options.MaxPixelsPerPage)
            {
                return FailedRun(job, started, RenderFailureCategory.ResourceLimitExceeded, pageCount);
            }
        }

        var rendered = new List<(RenderedPage Page, StoredBlob Blob)>();
        for (var p = 1; p <= pageCount; p++)
        {
            using var pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pageTimeout.CancelAfter(TimeSpan.FromSeconds(_options.RenderTimeoutSeconds));
            var page = _rasterizer.Render(pdf, p, _options.RenderDpi, pageTimeout.Token);
            if ((long)page.WidthPx * page.HeightPx > _options.MaxPixelsPerPage)
            {
                return FailedRun(job, started, RenderFailureCategory.ResourceLimitExceeded, pageCount);
            }

            using var png = new MemoryStream(page.Png, writable: false);
            var blob = await _store.WriteAsync("pages", png, ct);
            written.Add(blob.StorageKey);
            rendered.Add((page, blob));
        }

        var run = new DocumentRenderRun(job.Id, job.SourceDocumentId, _workerId, _rasterizer.RendererVersion, started, _clock.UtcNow,
            RenderRunOutcome.Succeeded, RenderFailureCategory.None, pageCount, _options.RenderDpi);
        foreach (var (page, blob) in rendered)
        {
            run.AddPage(page.PageNumber, page.WidthPx, page.HeightPx, blob.StorageKey, blob.Sha256, blob.Length);
        }

        return run;
    }

    private DocumentRenderRun FailedRun(DocumentRenderJob job, DateTimeOffset started, RenderFailureCategory category, int? pageCount = null)
        => new(job.Id, job.SourceDocumentId, _workerId, _rasterizer.RendererVersion, started, _clock.UtcNow, RenderRunOutcome.Failed, category, pageCount, _options.RenderDpi);

    private async Task UnwindAsync(List<string> keys)
    {
        foreach (var key in keys)
        {
            try { await _store.TryDeleteAsync(key); } catch { /* the orphan sweep is the backstop */ }
        }

        keys.Clear();
    }
}
