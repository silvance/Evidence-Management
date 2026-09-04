using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Emc.Application.Abstractions;
using Emc.Application.Ocr;
using Emc.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Emc.OcrWorker;

/// <summary>
/// Polls for queued work; processes one job at a time. Render jobs first (a document's pages
/// exist before anyone can ask for OCR over them), then OCR jobs. Backs off to the poll interval
/// when both queues are empty.
/// </summary>
public sealed class OcrWorkerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OcrOptions _options;
    private readonly ILogger<OcrWorkerService> _logger;

    public OcrWorkerService(IServiceScopeFactory scopes, IOptions<OcrOptions> options, ILogger<OcrWorkerService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OCR worker started; polling every {Seconds}s.", _options.PollSeconds);
        var nextSweep = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                using var scope = _scopes.CreateScope();

                // Blob-store reconciliation (OCR-018): at start and every OrphanSweepHours. Never
                // while a job is mid-flight in this process - it runs between jobs.
                if (_options.OrphanSweepHours > 0 && DateTimeOffset.UtcNow >= nextSweep)
                {
                    nextSweep = DateTimeOffset.UtcNow.AddHours(_options.OrphanSweepHours);
                    await scope.ServiceProvider.GetRequiredService<Emc.Application.Documents.IOrphanBlobSweeper>()
                        .SweepAsync(TimeSpan.FromHours(Math.Max(1, _options.OrphanGraceHours)), stoppingToken);
                }

                processed = await scope.ServiceProvider.GetRequiredService<Emc.Application.Documents.IDocumentRenderProcessor>().ProcessNextAsync(stoppingToken)
                    || await scope.ServiceProvider.GetRequiredService<IOcrJobProcessor>().ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Type only. The message may carry SQL text or engine output.
                _logger.LogError("OCR worker loop: {ExceptionType}; backing off.", ex.GetType().Name);
                processed = false;
            }

            if (!processed)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("OCR worker stopped.");
    }
}

/// <summary>
/// The worker is not a user. It holds no EMC role and no permission, and nothing it does goes
/// through the authorization service: it reads pages it is told to read and writes runs. This
/// principal exists only because infrastructure services take an ICurrentUser; it is
/// unauthenticated and carries no grants, so any code path that did consult authorization
/// would be denied.
/// </summary>
internal sealed class WorkerPrincipal : ICurrentUser
{
    public bool IsAuthenticated => false;
    public int UserId => 0;
    public string DisplayName => "OCR worker";
    public IReadOnlyCollection<RoleGrant> Grants => [];
}
