using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Documents;
using Emc.Domain.Ocr;
using Emc.Infrastructure.Documents;
using Emc.Infrastructure.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Phase 10 controls that are testable without the engine: nothing the engine read reaches a
/// log line or an audit row; the engine's work folder is cleaned even when it fails; the engine
/// is never started through a shell. Requirements: OCR-015, SEC-014.
/// </summary>
public class OcrSecurityTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _docOptions;
    private readonly FileSystemSourceDocumentStore _store;

    public OcrSecurityTests()
    {
        _docOptions = new SourceDocumentOptions { RootPath = _root, RenderDpi = 72 };
        _store = new FileSystemSourceDocumentStore(Options.Create(_docOptions));
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task NoExtractedTextReachesLogsOrAuditRows()
    {
        const string sensitive = "TESTSERIAL000777";
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Log test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "TEST ROOM", "FORT TEST", "SMITH, TEST A.", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "One item", "1", null, null, false, false, false, null));
        var documents = new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_docOptions));
        var upload = await documents.UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucher.Value, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, $"{sensitive}.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions);
        var jobs = new OcrJobService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store);
        Assert.True((await jobs.RequestAsync(upload.Value)).Succeeded);

        var log = new CapturingLogger<OcrJobProcessor>();
        var processor = new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine([(sensitive, 96m), ("IMEI", 95m)]), new Passthrough(), [new GenericLineTemplateMapper()],
            _harness.Clock, Options.Create(new OcrOptions { WorkerId = "w" }), log);
        Assert.True(await processor.ProcessNextAsync());

        // The run holds the text (that is its job); the log and the audit trail do not.
        var run = await _harness.Db.OcrRuns.AsNoTracking().Include(r => r.Fields).SingleAsync();
        Assert.Contains(run.Fields, f => f.RawText.Contains(sensitive, StringComparison.Ordinal));
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(sensitive, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains(".pdf", StringComparison.OrdinalIgnoreCase));
        var audit = await _harness.Db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(audit, a => (a.NewValue ?? "").Contains(sensitive, StringComparison.Ordinal) || (a.Reason ?? "").Contains(sensitive, StringComparison.Ordinal));

        // A failing engine: the type name is logged, never its message.
        await jobs.RequestAsync(upload.Value);
        var failing = new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine(new InvalidOperationException($"engine said: {sensitive}")), new Passthrough(), [new GenericLineTemplateMapper()],
            _harness.Clock, Options.Create(new OcrOptions { WorkerId = "w" }), log);
        Assert.True(await failing.ProcessNextAsync());
        Assert.Contains(log.Lines, l => l.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("engine said", StringComparison.Ordinal));
    }

    [TesseractFact]
    public async Task TheWorkFolderIsCleanedEvenWhenTheEngineIsGivenGarbage()
    {
        var work = Path.Combine(_root, "work");
        var engine = new TesseractProcessOcrEngine(Options.Create(new OcrOptions { EnginePath = TesseractFactAttribute.EnginePath!, TessdataPath = TesseractFactAttribute.TessdataPath!, WorkRoot = work, RequireApprovedArtifactHashes = false }));

        // Not an image at all. The engine fails; the category is what comes back; the folder is gone.
        var ex = await Assert.ThrowsAsync<OcrEngineException>(() => engine.RecognizeAsync([1, 2, 3, 4, 5]));
        Assert.Equal(OcrFailureCategory.EngineCrashed, ex.Category);
        Assert.DoesNotContain("1, 2, 3", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void TheEngineIsStartedWithAnArgumentList_NeverAShell()
    {
        var source = File.ReadAllText(Path.Combine(OfflineBuildTests.Root, "src", "Emc.Infrastructure", "Ocr", "TesseractProcessOcrEngine.cs"));
        Assert.Contains("UseShellExecute = false", source, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/bin/sh", source, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(dir, recursive: true)", source, StringComparison.Ordinal);
        Assert.Contains("info.Environment.Clear()", source, StringComparison.Ordinal);
    }

    private sealed class Passthrough : IImagePreprocessor
    {
        public string Version => "passthrough/1";
        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default) => new(png, 10, 10, 0, 0, sourceDpi);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
