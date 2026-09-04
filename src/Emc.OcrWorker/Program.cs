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

        // Constructing the engine verifies the binary and every model file (Phase 12). Do it before
        // the host reports "started", so a missing model is a start-up failure, not a queue that
        // silently never drains.
        var engine = host.Services.GetRequiredService<Emc.Application.Ocr.IOcrEngine>();
        host.Services.GetRequiredService<ILogger<OcrWorkerService>>().LogInformation(
            "OCR engine {Engine} {Version}; models {Models}; render helper configured.", engine.EngineName, engine.EngineVersion, engine.ModelIdentifiers);

        await host.RunAsync();
        return 0;
    }
}
