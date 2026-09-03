using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Emc.Infrastructure;
using Emc.OcrWorker;

// The OCR worker: a separate process from IIS, so that a parser of hostile input (PDFium,
// Tesseract) can crash, hang or be killed on timeout without taking the web application down
// (docs/architecture.md §9.1). It talks to nothing but SQL Server and the document store: jobs
// are rows in OcrJobs, results are rows in OcrRuns. No broker, no queue service, no network.
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

// A Windows Service host when installed as one; a console otherwise. The Windows Services
// integration package is not referenced (it is a NuGet package); running under the Service
// Control Manager is done through a service wrapper documented with the deployment, or by
// running the console under Task Scheduler at start-up. Either way the process is this one.
using var host = builder.Build();

// Constructing the engine verifies the binary and every model file (Phase 12). Do it before
// the host reports "started", so a missing model is a start-up failure, not a queue that
// silently never drains.
var engine = host.Services.GetRequiredService<Emc.Application.Ocr.IOcrEngine>();
host.Services.GetRequiredService<ILogger<OcrWorkerService>>().LogInformation(
    "OCR engine {Engine} {Version}; models {Models}.", engine.EngineName, engine.EngineVersion, engine.ModelIdentifiers);

await host.RunAsync();
