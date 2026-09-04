using System.Diagnostics;
using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Domain.Documents;
using Emc.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Rendering in a killable child process (DOC-014): the worker's own executable in "render"
/// mode, started per invocation with an argument list, killed on timeout, its output validated
/// as bytes rather than trusted as a claim. Requirements: DOC-014, DOC-015.
/// </summary>
public class DocumentRenderIsolationTests : IClassFixture<EmcWebFactory>, IDisposable
{
    private readonly EmcWebFactory _web;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));

    public DocumentRenderIsolationTests(EmcWebFactory web) => _web = web;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>The worker assembly is built beside the tests; the child is <c>dotnet Emc.OcrWorker.dll render ...</c>.</summary>
    private static string WorkerDll => Path.Combine(AppContext.BaseDirectory, "Emc.OcrWorker.dll");

    private IsolatedPdfRasterizer Isolated(string helperPath, int timeoutSeconds = 60)
        => new(Options.Create(new SourceDocumentOptions { RootPath = _root, RenderHelperPath = helperPath, RenderWorkRoot = Path.Combine(_root, "work"), RenderTimeoutSeconds = timeoutSeconds, RenderDpi = 72 }));

    [Fact]
    public void ThePageIsRenderedByTheChildProcess_AndTheOutputIsReadAsBytes()
    {
        Assert.True(File.Exists(WorkerDll), $"Worker assembly not found at {WorkerDll}.");
        var rasterizer = Isolated(WorkerDll);
        Assert.True(rasterizer.IsIsolated);
        Assert.Contains("isolated process", rasterizer.RendererVersion, StringComparison.Ordinal);

        var pdf = SyntheticPdf.Pages(2, "TEST ISOLATED RENDER");
        Assert.Equal(2, rasterizer.GetPageCount(pdf));
        var sizes = rasterizer.GetPageDimensions(pdf);
        Assert.Equal(2, sizes.Count);
        Assert.InRange(sizes[0].WidthPoints, 611, 613);
        Assert.InRange(sizes[0].HeightPoints, 791, 793);

        var page = rasterizer.Render(pdf, 2, 72);
        Assert.Equal(2, page.PageNumber);
        Assert.InRange(page.WidthPx, 600, 620);
        Assert.InRange(page.HeightPx, 780, 800);
        Assert.Equal((page.WidthPx, page.HeightPx), IsolatedPdfRasterizer.ReadPngDimensions(page.Png));

        // The per-invocation folders are gone.
        var work = Path.Combine(_root, "work");
        Assert.True(!Directory.Exists(work) || Directory.GetDirectories(work).Length == 0);
    }

    [Fact]
    public void AMalformedDocumentIsReportedByExitCode_NotByText()
    {
        var rasterizer = Isolated(WorkerDll);
        var ex = Assert.Throws<MalformedPdfException>(() => rasterizer.GetPageCount(SyntheticPdf.FakePdf()));
        Assert.DoesNotContain("PNG", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void TheHelperRefusesBadArguments()
    {
        // Exit code 3: a usage error, never an attempt to interpret anything.
        var psi = new ProcessStartInfo(IsolatedPdfRasterizer.ResolveDotnetHost()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(WorkerDll);
        psi.ArgumentList.Add("render");
        psi.ArgumentList.Add("page");
        psi.ArgumentList.Add("--page");
        psi.ArgumentList.Add("0");
        using var process = Process.Start(psi)!;
        process.WaitForExit(30_000);
        Assert.Equal(IsolatedPdfRasterizer.ExitUsage, process.ExitCode);
    }

    [Fact]
    public async Task AHungHelperIsKilledOnTimeout_AndTheJobIsRequeuedAsATimeout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // the hanging stand-in is a shell script; the Windows lane covers the helper through the real executable
        }

        Directory.CreateDirectory(_root);
        var hang = Path.Combine(_root, "hang.sh");
        await File.WriteAllTextAsync(hang, "#!/bin/sh\nsleep 300\n");
        File.SetUnixFileMode(hang, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var rasterizer = Isolated(hang, timeoutSeconds: 1);
        var watch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => rasterizer.GetPageCount(SyntheticPdf.SinglePage()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"The hung helper took {watch.Elapsed} to be killed.");

        // Through the processor: a Timeout run, the job requeued for another attempt.
        using var harness = new SliceTestHarness();
        var options = new SourceDocumentOptions { RootPath = _root, RenderHelperPath = hang, RenderWorkRoot = Path.Combine(_root, "work"), RenderTimeoutSeconds = 1, RenderDpi = 72 };
        var store = new FileSystemSourceDocumentStore(Options.Create(options));
        harness.SignInAsAgent();
        var caseResult = await harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Render test", null, harness.EvidenceRoomId));
        var voucher = await harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A.", harness.Clock.UtcNow, false, null));
        var documents = new SourceDocumentService(harness.Db, harness.Authorization, harness.CurrentUser, harness.Audit, harness.Clock, store, Options.Create(options));
        var upload = await documents.UploadAsync(new UploadSourceDocumentRequest(harness.EvidenceRoomId, null, voucher.Value, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        Assert.True(upload.Succeeded, upload.Error);

        Assert.True(await TestRendering.Processor(harness.Db, store, harness.Clock, options, rasterizer).ProcessNextAsync());
        var job = await harness.Db.DocumentRenderJobs.AsNoTracking().SingleAsync(j => j.SourceDocumentId == upload.Value);
        Assert.Equal(RenderJobStatus.Queued, job.Status);
        Assert.Equal(RenderFailureCategory.Timeout, job.LastFailureCategory);
        Assert.Equal(1, job.Attempts);
        var run = await harness.Db.DocumentRenderRuns.AsNoTracking().SingleAsync(r => r.SourceDocumentId == upload.Value);
        Assert.Equal(RenderRunOutcome.Failed, run.Outcome);
        Assert.Equal(RenderFailureCategory.Timeout, run.FailureCategory);

        // Nothing named "sleep 300" from our folder survives.
        using var check = Process.Start(new ProcessStartInfo("pgrep", ["-f", hang]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        var survivors = await check.StandardOutput.ReadToEndAsync();
        check.WaitForExit();
        Assert.True(string.IsNullOrWhiteSpace(survivors), "The hung helper process survived the kill.");
    }

    [Fact]
    public void TheWebHostRegistersNoRasterizer_AndNoRenderProcessor()
    {
        // DOC-014 at the composition root: the web process cannot render even by accident.
        Assert.Null(_web.Services.GetService<IPdfRasterizer>());
        Assert.Null(_web.Services.GetService<IDocumentRenderProcessor>());
        Assert.NotNull(_web.Services.GetService<ISourceDocumentStore>());
    }

    [Fact]
    public void ThePngHeaderIsValidatedNotTrusted()
    {
        Assert.Throws<RendererCrashedException>(() => IsolatedPdfRasterizer.ReadPngDimensions([1, 2, 3]));
        byte[] zeroSized = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<RendererCrashedException>(() => IsolatedPdfRasterizer.ReadPngDimensions(zeroSized)); // a PNG signature and IHDR, but no size
        Assert.Equal((1, 1), IsolatedPdfRasterizer.ReadPngDimensions(SyntheticPdf.FakePdf()));
    }
}
