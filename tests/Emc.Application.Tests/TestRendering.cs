using Emc.Application.Abstractions;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Common;
using Emc.Infrastructure.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Emc.Application.Tests;

/// <summary>
/// The render worker in miniature for tests: drains the render-job queue through
/// <see cref="DocumentRenderProcessor"/> with PDFium in-process (no helper path), exactly as the
/// deployed worker does through its child process. Tests that need page images call this after
/// an upload, because the web process never renders (DOC-014).
/// </summary>
public static class TestRendering
{
    public static DocumentRenderProcessor Processor(IEmcDbContext db, ISourceDocumentStore store, IClock clock, SourceDocumentOptions options, IPdfRasterizer? rasterizer = null, string workerId = "render-test")
        => new(db, store, rasterizer ?? new IsolatedPdfRasterizer(Options.Create(options)), clock, Options.Create(options),
               Options.Create(new OcrOptions { WorkerId = workerId }), NullLogger<DocumentRenderProcessor>.Instance);

    /// <summary>Processes every queued render job. Returns how many were processed.</summary>
    public static async Task<int> RenderAllAsync(IEmcDbContext db, ISourceDocumentStore store, IClock clock, SourceDocumentOptions options, IPdfRasterizer? rasterizer = null, string workerId = "render-test")
    {
        var processor = Processor(db, store, clock, options, rasterizer, workerId);
        var processed = 0;
        while (await processor.ProcessNextAsync())
        {
            processed++;
        }

        return processed;
    }
}
