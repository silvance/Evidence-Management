using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Emc.Application.Abstractions;
using Emc.Application.Ocr;
using Emc.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Emc.OcrWorker;

/// <summary>Polls for queued jobs; processes one at a time. Backs off to the poll interval when the queue is empty.</summary>
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
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                using var scope = _scopes.CreateScope();
                processed = await scope.ServiceProvider.GetRequiredService<IOcrJobProcessor>().ProcessNextAsync(stoppingToken);
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
