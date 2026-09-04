using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Items;
using Emc.Domain.Common;
using Emc.Infrastructure.Persistence;
using Emc.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Emc.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEmcInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<EmcDbContext>(options => options
            .UseSqlServer(connectionString, sql => sql
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), null))

            // Accountability data must never be silently stale. Explicit tracking behaviour, and
            // no lazy loading, so a query that needs history says so.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll));

        services.AddScoped<IEmcDbContext>(sp => sp.GetRequiredService<EmcDbContext>());

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IAuditRecorder, AuditRecorder>();
        services.AddScoped<IEvidenceAuthorizationService, EvidenceAuthorizationService>();
        services.AddScoped<IItemEventRecorder, ItemEventRecorder>();

        services.AddScoped<Emc.Application.Reads.IEvidenceReadService,
            Emc.Application.Reads.EvidenceReadService>();

        services.AddScoped<ICaseService, CaseService>();
        services.AddScoped<IVoucherService, VoucherService>();
        services.AddScoped<ITemporaryIdentifierAllocator, TemporaryIdentifierAllocator>();
        services.AddScoped<IEvidenceIntakeService, EvidenceIntakeService>();
        services.AddScoped<IItemHistoryService, ItemHistoryService>();
        services.AddScoped<ICustodyEventService, CustodyEventService>();
        services.AddScoped<Emc.Application.Filing.IPhysicalDocumentService, Emc.Application.Filing.PhysicalDocumentService>();
        services.AddScoped<Emc.Application.Filing.IPhysicalDigitalConsistencyService, Emc.Application.Filing.PhysicalDigitalConsistencyService>();
        services.AddScoped<Emc.Application.Filing.IRetentionDashboardService, Emc.Application.Filing.RetentionDashboardService>();
        services.AddScoped<Emc.Application.Suspense.ITemporaryReleaseService, Emc.Application.Suspense.TemporaryReleaseService>();
        services.AddScoped<Emc.Application.Suspense.ISuspenseDashboardService, Emc.Application.Suspense.SuspenseDashboardService>();

        // Source documents: immutable filesystem store outside the web root and the
        // receipt/view/download service. NO rasterizer is registered here: the web process never
        // parses a PDF (DOC-014). It validates the envelope, stores and hashes the bytes and
        // queues a render job; the worker renders in a killable child process. See AddEmcOcrWorker.
        services.AddOptions<Emc.Application.Documents.SourceDocumentOptions>()
            .BindConfiguration(Emc.Application.Documents.SourceDocumentOptions.SectionName);
        services.AddSingleton<Emc.Application.Documents.ISourceDocumentStore, Emc.Infrastructure.Documents.FileSystemSourceDocumentStore>();
        services.AddScoped<Emc.Application.Documents.ISourceDocumentService, Emc.Application.Documents.SourceDocumentService>();
        // OCR, web side: request, status, verification. The engine is NOT registered here; the
        // web process never runs it (Phase 3C). See AddEmcOcrWorker.
        services.AddOptions<Emc.Application.Ocr.OcrOptions>().BindConfiguration(Emc.Application.Ocr.OcrOptions.SectionName);
        services.AddScoped<Emc.Application.Ocr.IOcrJobService, Emc.Application.Ocr.OcrJobService>();
        services.AddScoped<Emc.Application.Reconciliation.IReconciliationService, Emc.Application.Reconciliation.ReconciliationService>();
        services.AddScoped<Emc.Application.Integrity.IIntegrityVerificationService,
            Emc.Application.Integrity.IntegrityVerificationService>();

        // AUD-011 / AUD-020. Local evidence times are interpreted in the evidence room's zone,
        // never the host's; this is the only route to that interpretation.
        services.AddScoped<Emc.Application.Time.IEvidenceRoomTimeService,
            Emc.Application.Time.EvidenceRoomTimeService>();

        return services;
    }

    /// <summary>
    /// The worker's additions: the isolated PDF rasterizer and the render-job processor
    /// (DOC-014, DOC-015); the Tesseract process engine, the Skia preprocessor, the template
    /// mappers in identification order (fallback last), and the OCR job processor. Called only
    /// by Emc.OcrWorker. Constructing the engine verifies the binary and the models are present
    /// and fails the host's start-up otherwise (Phase 12).
    /// </summary>
    public static IServiceCollection AddEmcOcrWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<Emc.Application.Documents.IPdfRasterizer, Emc.Infrastructure.Documents.IsolatedPdfRasterizer>();
        services.AddScoped<Emc.Application.Documents.IDocumentRenderProcessor, Emc.Application.Documents.DocumentRenderProcessor>();
        services.AddScoped<Emc.Application.Documents.IOrphanBlobSweeper, Emc.Application.Documents.OrphanBlobSweeper>();
        services.AddSingleton<Emc.Application.Ocr.IOcrEngine, Emc.Infrastructure.Ocr.TesseractProcessOcrEngine>();
        services.AddSingleton<Emc.Application.Ocr.IImagePreprocessor>(sp =>
            new Emc.Infrastructure.Ocr.SkiaImagePreprocessor(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Emc.Application.Ocr.OcrOptions>>().Value.TargetDpi));
        // Identification order: the DA Form 4137 mapper first; the generic fallback LAST.
        services.AddSingleton<Emc.Application.Ocr.IFormTemplateMapper, Emc.Application.Ocr.DaForm4137.DaForm4137TemplateMapper>();
        services.AddSingleton<Emc.Application.Ocr.IFormTemplateMapper, Emc.Application.Ocr.GenericLineTemplateMapper>();
        services.AddScoped<Emc.Application.Ocr.IOcrJobProcessor, Emc.Application.Ocr.OcrJobProcessor>();
        return services;
    }
}
