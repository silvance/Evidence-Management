using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Emc.Infrastructure;

namespace Emc.OcrWorker;

/// <summary>Entry point. An explicit class (not top-level statements) so that the test project can reference this executable beside the web application without two global <c>Program</c> types.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // "render": this executable started by the worker itself as a killable child process to
        // rasterize one PDF (DOC-014). No host, no configuration, no database. Handled before anything
        // else so the child touches nothing the service does.
        if (args.Length > 0 && string.Equals(args[0], "render", StringComparison.Ordinal))
        {
            return RenderHelper.Run(args);
        }

        // The worker: a separate process from IIS, so that a parser of hostile input (PDFium,
        // Tesseract) can crash, hang or be killed on timeout without taking the web application down
        // (docs/architecture.md §9.1). It talks to nothing but SQL Server and the document store: jobs
        // are rows in DocumentRenderJobs and OcrJobs, results are rows in DocumentRenderRuns and OcrRuns.
        // No broker, no queue service, no network.
        //
        // Runs as a Windows Service (or a console for diagnosis) under a dedicated low-privilege
        // account; see docs/architecture.md §9.1 for the ACLs it needs and nothing more.
        var builder = Host.CreateApplicationBuilder(args);

        // Windows Service host (P13). Under the Service Control Manager the process answers
        // start/stop control requests, logs to the Application event log through the host's
        // logging (identifiers and categories only, never content), and stops cleanly: the loop
        // observes the stopping token, a leased job's lease is left to expire, and a render
        // child in flight is killed by its parent's timeout. Run from a console, the same
        // process is a console application - nothing else changes.
        builder.Services.AddWindowsService(o => o.ServiceName = "EmcOcrWorker");

        var connectionString = builder.Configuration.GetConnectionString("Emc")
            ?? throw new InvalidOperationException("Connection string 'Emc' is not configured. See appsettings.json.");

        builder.Services.AddEmcInfrastructure(connectionString);
        builder.Services.AddEmcOcrWorker();
        builder.Services.AddSingleton<Emc.Application.Abstractions.ICurrentUser, WorkerPrincipal>();
        builder.Services.AddHostedService<OcrWorkerService>();

        using var host = builder.Build();

        // The render helper must be configured and present: without it PDFium would parse hostile
        // bytes inside the worker process itself, which is the thing DOC-014 forbids in production.
        var documentOptions = host.Services.GetRequiredService<IOptions<Emc.Application.Documents.SourceDocumentOptions>>().Value;
        if (string.IsNullOrWhiteSpace(documentOptions.RenderHelperPath))
        {
            throw new InvalidOperationException("SourceDocuments:RenderHelperPath is not configured. Point it at this worker's own executable (Emc.OcrWorker.exe); pages are rendered in a child process, never in the service.");
        }

        if (!File.Exists(documentOptions.RenderHelperPath))
        {
            throw new InvalidOperationException("SourceDocuments:RenderHelperPath does not name an existing file.");
        }

        // Constructing the engine verifies every installed artifact against the approved hashes
        // (OCR-017) and executes the binary for its version (Phase 12). Do it before the host
        // reports "started", so an unapproved binary or a missing model is a start-up failure,
        // not a queue that silently never drains. The category is logged; the exception is not.
        var ocrOptions = host.Services.GetRequiredService<IOptions<Emc.Application.Ocr.OcrOptions>>().Value;
        if (ocrOptions.RequireApprovedArtifactHashes && (ocrOptions.ApprovedArtifactHashes is null || ocrOptions.ApprovedArtifactHashes.Count == 0))
        {
            throw new InvalidOperationException("Ocr:ApprovedArtifactHashes is empty. Copy the approved SHA-256 of tesseract.exe, eng.traineddata and osd.traineddata from the reviewed artifact manifest; the worker does not run an engine nobody approved (OCR-017).");
        }

        Emc.Application.Ocr.IOcrEngine engine;
        try
        {
            engine = host.Services.GetRequiredService<Emc.Application.Ocr.IOcrEngine>();
        }
        catch (Emc.Application.Ocr.OcrEngineException ex)
        {
            host.Services.GetRequiredService<ILogger<OcrWorkerService>>().LogCritical("OCR engine start-up check failed: {Category}. The worker will not start.", ex.Category);
            return 2;
        }

        host.Services.GetRequiredService<ILogger<OcrWorkerService>>().LogInformation(
            "OCR engine {Engine} {Version}; models {Models}; artifacts verified against approved hashes: {Verified}; render helper configured.",
            engine.EngineName, engine.EngineVersion, engine.ModelIdentifiers,
            engine is Emc.Infrastructure.Ocr.TesseractProcessOcrEngine t && t.ArtifactsVerifiedAgainstApprovedHashes);

        await host.RunAsync();
        return 0;
    }
}
