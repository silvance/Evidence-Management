using Emc.Application.Abstractions;
using Emc.Application.Documents;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Domain.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emc.Application.Ocr;

/// <summary>
/// The worker's loop body: lease one job, read the document's rendered pages from the store,
/// preprocess, recognize, identify the template, map fields, write ONE immutable run, settle the
/// job. Runs in <c>Emc.OcrWorker</c>, never in the web process (Phase 3C).
///
/// Logging discipline (Phase 10): log lines carry job id, document id, page counts, durations
/// and failure CATEGORIES. Never a word the engine read, never an exception message from the
/// engine, never a filename.
/// </summary>
public interface IOcrJobProcessor
{
    /// <summary>Processes at most one job. Returns false when nothing was available.</summary>
    Task<bool> ProcessNextAsync(CancellationToken ct = default);
}

public sealed class OcrJobProcessor : IOcrJobProcessor
{
    private readonly IEmcDbContext _db;
    private readonly ISourceDocumentStore _store;
    private readonly IOcrEngine _engine;
    private readonly IImagePreprocessor _preprocessor;
    private readonly IReadOnlyList<IFormTemplateMapper> _templates;
    private readonly IClock _clock;
    private readonly OcrOptions _options;
    private readonly ILogger<OcrJobProcessor> _logger;
    private readonly string _workerId;

    public OcrJobProcessor(
        IEmcDbContext db,
        ISourceDocumentStore store,
        IOcrEngine engine,
        IImagePreprocessor preprocessor,
        IEnumerable<IFormTemplateMapper> templates,
        IClock clock,
        IOptions<OcrOptions> options,
        ILogger<OcrJobProcessor> logger)
    {
        _db = db;
        _store = store;
        _engine = engine;
        _preprocessor = preprocessor;
        _templates = templates.ToList();
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _workerId = string.IsNullOrWhiteSpace(_options.WorkerId)
            ? $"{Environment.MachineName}/{Environment.ProcessId}"
            : _options.WorkerId.Trim();

        if (_templates.Count == 0)
        {
            throw new InvalidOperationException("At least one form template mapper is required.");
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        var job = await LeaseNextAsync(ct);
        if (job is null)
        {
            return false;
        }

        var started = _clock.UtcNow;
        _logger.LogInformation("OCR job {JobId} leased by {WorkerId} for document {DocumentId} (attempt {Attempt}).", job.Id, _workerId, job.SourceDocumentId, job.Attempts);

        OcrRun run;
        try
        {
            using var jobTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            jobTimeout.CancelAfter(TimeSpan.FromSeconds(_options.JobTimeoutSeconds));
            run = await ExecuteAsync(job, started, jobTimeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: leave the lease to expire; another worker (or this one, restarted) retries.
            _logger.LogWarning("OCR job {JobId}: worker stopping; lease left to expire.", job.Id);
            throw;
        }
        catch (OperationCanceledException)
        {
            run = FailedRun(job, started, OcrFailureCategory.Timeout);
        }
        catch (OcrEngineException ex)
        {
            run = FailedRun(job, started, ex.Category);
        }
        catch (DomainRuleViolationException)
        {
            run = FailedRun(job, started, OcrFailureCategory.Unexpected);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The type is logged; the message is not (it may quote engine output).
            _logger.LogError("OCR job {JobId}: unexpected {ExceptionType}.", job.Id, ex.GetType().Name);
            run = FailedRun(job, started, OcrFailureCategory.Unexpected);
        }

        _db.OcrRuns.Add(run);
        if (run.Outcome == OcrRunOutcome.Succeeded)
        {
            job.Complete(_workerId, _clock.UtcNow);
        }
        else
        {
            job.Fail(_workerId, _clock.UtcNow, run.FailureCategory, _options.MaxAttempts);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("OCR job {JobId}: {Outcome} ({Category}); {Fields} field(s) over {Pages} page(s) in {Ms} ms; job now {Status}.",
            job.Id, run.Outcome, run.FailureCategory, run.Fields.Count, run.PagesProcessed, (int)(run.CompletedAtUtc - run.StartedAtUtc).TotalMilliseconds, job.Status);
        return true;
    }

    private async Task<OcrJob?> LeaseNextAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var candidates = await _db.OcrJobs
            .Where(j => j.Status == OcrJobStatus.Queued || (j.Status == OcrJobStatus.Running && j.LeaseExpiresUtc != null && j.LeaseExpiresUtc <= now))
            .OrderBy(j => j.RequestedAtUtc)
            .Take(5)
            .ToListAsync(ct);

        foreach (var job in candidates)
        {
            try
            {
                job.Lease(_workerId, now, TimeSpan.FromSeconds(_options.LeaseSeconds), _options.MaxAttempts);
                await _db.SaveChangesAsync(ct);
                return job;
            }
            catch (DomainRuleViolationException)
            {
                // Attempts exhausted: the job was marked Failed by Lease; persist and move on.
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker took it. Detach and try the next one.
                foreach (var entry in ((DbContext)_db).ChangeTracker.Entries<OcrJob>().Where(e => e.Entity.Id == job.Id).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        return null;
    }

    private async Task<OcrRun> ExecuteAsync(OcrJob job, DateTimeOffset started, CancellationToken ct)
    {
        var document = await _db.SourceDocuments.AsNoTracking().Include(d => d.Pages)
            .FirstOrDefaultAsync(d => d.Id == job.SourceDocumentId, ct);
        if (document is null || document.ImportStatus != SourceDocumentImportStatus.Rendered || document.Pages.Count == 0)
        {
            return FailedRun(job, started, OcrFailureCategory.DocumentUnavailable);
        }

        var pages = new List<RecognizedPage>();
        var toProcess = document.Pages.OrderBy(p => p.PageNumber).Take(_options.MaxPagesPerJob).ToList();
        foreach (var page in toProcess)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await _store.OpenReadAsync(page.StorageKey, ct);
            if (stream is null)
            {
                return FailedRun(job, started, OcrFailureCategory.DocumentUnavailable);
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var png = buffer.ToArray();

            using var pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pageTimeout.CancelAfter(TimeSpan.FromSeconds(_options.PageTimeoutSeconds));

            var recognized = await RecognizeUprightAsync(page.PageNumber, png, page.RenderDpi, pageTimeout.Token);
            if (recognized is null)
            {
                return FailedRun(job, started, OcrFailureCategory.ResourceLimitExceeded);
            }

            pages.Add(recognized);
        }

        // Template identification: first mapper whose score clears its own threshold.
        IFormTemplateMapper? chosen = null;
        var identified = false;
        foreach (var template in _templates)
        {
            var score = template.Identify(pages);
            if (score >= template.IdentificationThreshold && (template.IdentificationThreshold > 0 || chosen is null))
            {
                chosen = template;
                identified = template.IdentificationThreshold > 0;
                if (identified)
                {
                    break;
                }
            }
        }

        if (chosen is null)
        {
            return FailedRun(job, started, OcrFailureCategory.TemplateNotIdentified);
        }

        var candidates = chosen.Map(pages);
        var run = new OcrRun(
            job.Id, job.SourceDocumentId, _workerId, _engine.EngineName, _engine.EngineVersion, _engine.ModelIdentifiers,
            _preprocessor.Version, chosen.TemplateId, identified, started, _clock.UtcNow,
            OcrRunOutcome.Succeeded, OcrFailureCategory.None, pages.Count);

        foreach (var c in candidates)
        {
            run.AddField(c.FieldKey, c.PageNumber, c.RawText, c.NormalizedCandidate, c.Confidence, c.Left, c.Top, c.Width, c.Height);
        }

        return run;
    }

    /// <summary>OSD orientation confidence at or above this is trusted without a vote.</summary>
    internal const decimal TrustedOrientationConfidence = 5m;

    /// <summary>
    /// Orientation. The engine's OSD is reliable on a text-rich page and useless on a sparse one
    /// (it reports low confidence). When it is not trusted, the page is recognized at 0° and
    /// 180° - the two orientations a flatbed or feeder produces - and, if neither reads well,
    /// at 90° and 270°; the orientation whose words the engine was most confident in wins.
    /// The cost is extra engine passes on sparse pages, in a worker, which is acceptable.
    /// Returns null when the preprocessed image exceeds the pixel limit.
    /// </summary>
    private async Task<RecognizedPage?> RecognizeUprightAsync(int pageNumber, byte[] png, int sourceDpi, CancellationToken ct)
    {
        var osd = await _engine.DetectOrientationAsync(png, ct);
        if (osd.Confidence >= TrustedOrientationConfidence)
        {
            return await RecognizeAtAsync(pageNumber, png, sourceDpi, osd.RotateClockwiseDegrees, ct);
        }

        RecognizedPage? best = null;
        var bestScore = -1m;
        foreach (var degrees in new[] { 0, 180, 90, 270 })
        {
            var candidate = await RecognizeAtAsync(pageNumber, png, sourceDpi, degrees, ct);
            if (candidate is null)
            {
                return null;
            }

            var score = OrientationScore(candidate.Result);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }

            // Two good candidates (0/180) seen and one reads clearly: no need for the sideways pair.
            if (degrees == 180 && bestScore >= 300m)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>Sum of confidence over words the engine was at least moderately sure of; a wrong orientation yields few such words.</summary>
    internal static decimal OrientationScore(OcrPageResult result)
        => result.Words.Where(w => w.Confidence >= 60m && w.Text.Any(char.IsLetterOrDigit)).Sum(w => w.Confidence);

    private async Task<RecognizedPage?> RecognizeAtAsync(int pageNumber, byte[] png, int sourceDpi, int degrees, CancellationToken ct)
    {
        var image = _preprocessor.Preprocess(png, sourceDpi, degrees, ct);
        if ((long)image.Width * image.Height > _options.MaxPixelsPerPage)
        {
            return null;
        }

        var result = await _engine.RecognizeAsync(image.Png, ct);
        return new RecognizedPage(pageNumber, result, image);
    }

    private OcrRun FailedRun(OcrJob job, DateTimeOffset started, OcrFailureCategory category)
        => new(job.Id, job.SourceDocumentId, _workerId, _engine.EngineName, _engine.EngineVersion, _engine.ModelIdentifiers,
               _preprocessor.Version, null, false, started, _clock.UtcNow, OcrRunOutcome.Failed, category, 0);
}
