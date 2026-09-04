using System.Diagnostics;
using System.Security.Cryptography;
using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Documents;
using Emc.Domain.Ocr;
using Emc.Infrastructure.Documents;
using Emc.Infrastructure.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The worker's transactional discipline: one open job per document at the database, leases
/// renewed page by page and settled only by their holder, page blobs never left without the
/// run that references them, the blob store reconciled against the database, and the installed
/// engine verified against approved hashes before it is ever executed.
/// Requirements: OCR-010, OCR-011, OCR-017, OCR-018, DOC-014.
/// </summary>
public class OcrLeaseAndBlobTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _docOptions;
    private readonly FileSystemSourceDocumentStore _store;

    public OcrLeaseAndBlobTests()
    {
        _docOptions = new SourceDocumentOptions { RootPath = _root, RenderDpi = 72 };
        _store = new FileSystemSourceDocumentStore(Options.Create(_docOptions));
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<int> RenderedDocumentAsync(int pages = 1)
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Lease test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A.", _harness.Clock.UtcNow, false, null));
        var documents = new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_docOptions));
        var upload = await documents.UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucher.Value, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.Pages(pages), "UNCLASSIFIED"));
        Assert.True(upload.Succeeded, upload.Error);
        Assert.Equal(1, await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions));
        return upload.Value;
    }

    private IOcrJobService Jobs() => new OcrJobService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store);

    private OcrJobProcessor Processor(IOcrEngine engine, string workerId, ISourceDocumentStore? store = null, Emc.Infrastructure.Persistence.EmcDbContext? db = null)
        => new(db ?? _harness.Db, store ?? _store, engine, new Passthrough(), [new GenericLineTemplateMapper()], _harness.Clock,
               Options.Create(new OcrOptions { WorkerId = workerId, LeaseSeconds = 600, PageTimeoutSeconds = 60 }), NullLogger<OcrJobProcessor>.Instance);

    [Fact]
    public async Task TheDatabaseRefusesASecondOpenJobForOneDocument()
    {
        // OCR-010 / DOC-014 at the database: two requests racing past the application's check
        // cannot both land. A closed job beside an open one is fine.
        var documentId = await RenderedDocumentAsync();
        var renderRunId = (await _harness.Db.DocumentRenderRuns.AsNoTracking().SingleAsync(r => r.SourceDocumentId == documentId)).Id;

        _harness.Db.OcrJobs.Add(new OcrJob(documentId, renderRunId, _harness.EvidenceRoomId, _harness.AgentUserId, _harness.Clock.UtcNow));
        await _harness.Db.SaveChangesAsync();
        _harness.Db.OcrJobs.Add(new OcrJob(documentId, renderRunId, _harness.EvidenceRoomId, _harness.AgentUserId, _harness.Clock.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.ChangeTracker.Clear();

        _harness.Db.DocumentRenderJobs.Add(new DocumentRenderJob(documentId, _harness.EvidenceRoomId, _harness.AgentUserId, _harness.Clock.UtcNow));
        await _harness.Db.SaveChangesAsync();
        _harness.Db.DocumentRenderJobs.Add(new DocumentRenderJob(documentId, _harness.EvidenceRoomId, _harness.AgentUserId, _harness.Clock.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.ChangeTracker.Clear();

        // Settle the open OCR job; a new one is then allowed beside the closed one.
        var open = await _harness.Db.OcrJobs.SingleAsync(j => j.SourceDocumentId == documentId);
        open.Lease("w", _harness.Clock.UtcNow, TimeSpan.FromMinutes(1));
        open.Fail("w", _harness.Clock.UtcNow, OcrFailureCategory.ModelMissing);
        await _harness.Db.SaveChangesAsync();
        _harness.Db.OcrJobs.Add(new OcrJob(documentId, renderRunId, _harness.EvidenceRoomId, _harness.AgentUserId, _harness.Clock.UtcNow));
        await _harness.Db.SaveChangesAsync();
        Assert.Equal(2, await _harness.Db.OcrJobs.CountAsync(j => j.SourceDocumentId == documentId));
    }

    [Fact]
    public async Task TheLeaseIsRenewedAfterEveryPage()
    {
        // OCR-011. A three-page job: the expiry seen from another context moves forward as the
        // pages go by, so a long job is never taken over while it is genuinely running.
        var documentId = await RenderedDocumentAsync(pages: 3);
        Assert.True((await Jobs().RequestAsync(documentId)).Succeeded);

        using var observer = _harness.CreateSecondContext();
        var expiries = new List<DateTimeOffset?>();
        var engine = new HookedEngine(async () =>
        {
            _harness.Clock.Advance(TimeSpan.FromSeconds(30));
            expiries.Add(await observer.OcrJobs.AsNoTracking().Where(j => j.SourceDocumentId == documentId).Select(j => j.LeaseExpiresUtc).SingleAsync());
        });

        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());

        Assert.Equal(3, expiries.Count);
        Assert.True(expiries[1] > expiries[0], "The lease was not renewed after the first page.");
        Assert.True(expiries[2] > expiries[1], "The lease was not renewed after the second page.");
        var job = await _harness.Db.OcrJobs.AsNoTracking().SingleAsync(j => j.SourceDocumentId == documentId);
        Assert.Equal(OcrJobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task ALeaseLostBeforeSettlementDiscardsTheAttempt_AndRemovesItsBlobs()
    {
        // OCR-011 / OCR-018. Worker A writes its page images, but worker B took the lease over in
        // between (A was presumed dead). A's settlement conflicts: A's run is not the record, its
        // blobs are removed, and nothing in the store is left without a row that names it.
        var documentId = await RenderedDocumentAsync();
        Assert.True((await Jobs().RequestAsync(documentId)).Succeeded);

        using var workerB = _harness.CreateSecondContext();
        var store = new InterceptingStore(_store, onOcrPageWritten: async () =>
        {
            // The lease A holds expires (as far as the clock is concerned) and B takes it.
            _harness.Clock.Advance(TimeSpan.FromMinutes(20));
            var job = await workerB.OcrJobs.SingleAsync(j => j.SourceDocumentId == documentId);
            job.Lease("worker-b", _harness.Clock.UtcNow, TimeSpan.FromMinutes(10));
            await workerB.SaveChangesAsync();
        });

        Assert.True(await Processor(new OcrProcessorTests.FakeEngine([("TEST", 95m)]), "worker-a", store).ProcessNextAsync());

        _harness.Db.ChangeTracker.Clear();
        var settled = await _harness.Db.OcrJobs.AsNoTracking().SingleAsync(j => j.SourceDocumentId == documentId);
        Assert.Equal(OcrJobStatus.Running, settled.Status);
        Assert.Equal("worker-b", settled.LeasedByWorkerId);
        Assert.Empty(await _harness.Db.OcrRuns.AsNoTracking().Where(r => r.SourceDocumentId == documentId).ToListAsync());

        Assert.Single(store.OcrPageKeys);
        Assert.Null(await _store.OpenReadAsync(store.OcrPageKeys[0]));

        // Every committed blob in the store is referenced by a row.
        var referenced = new HashSet<string>(await _harness.Db.SourceDocuments.Select(d => d.StorageKey).ToListAsync());
        referenced.UnionWith(await _harness.Db.DocumentRenderPages.Select(p => p.StorageKey).ToListAsync());
        var committed = (await _store.EnumerateAsync()).Where(e => e.State == StoredBlobState.Committed).Select(e => e.StorageKey).ToList();
        Assert.All(committed, key => Assert.Contains(key, referenced));
    }

    [Fact]
    public async Task TheSweepRemovesOnlyOldUnreferencedBlobsAndOldPartials()
    {
        // OCR-018. Referenced: kept at any age. Unreferenced and young: staged, kept.
        // Unreferenced and old: orphan, removed. Old partial: removed.
        var documentId = await RenderedDocumentAsync();
        var referencedKeys = new HashSet<string>(await _harness.Db.SourceDocuments.Select(d => d.StorageKey).ToListAsync());
        referencedKeys.UnionWith(await _harness.Db.DocumentRenderPages.Select(p => p.StorageKey).ToListAsync());
        Assert.Equal(2, referencedKeys.Count);

        var oldOrphan = await _store.WriteAsync("ocr-pages", new MemoryStream([1, 2, 3]));
        var youngOrphan = await _store.WriteAsync("ocr-pages", new MemoryStream([4, 5, 6]));
        var oldPartial = Path.Combine(_root, "pages", "2026", "01", "deadbeef.bin.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPartial)!);
        await File.WriteAllBytesAsync(oldPartial, [7]);

        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(Path.Combine(_root, oldOrphan.StorageKey.Replace('/', Path.DirectorySeparatorChar)), twoDaysAgo);
        File.SetLastWriteTimeUtc(oldPartial, twoDaysAgo);
        foreach (var key in referencedKeys)
        {
            File.SetLastWriteTimeUtc(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)), twoDaysAgo); // old AND referenced: still kept
        }

        var sweeper = new OrphanBlobSweeper(_harness.Db, _store, new TestClock(DateTimeOffset.UtcNow), NullLogger<OrphanBlobSweeper>.Instance);
        var report = await sweeper.SweepAsync(TimeSpan.FromHours(24));

        Assert.Equal(5, report.Enumerated);
        Assert.Equal(2, report.Referenced);
        Assert.Equal(1, report.StagedLeft);
        Assert.Equal(1, report.PartialsRemoved);
        Assert.Equal(1, report.OrphansRemoved);
        Assert.Null(await _store.OpenReadAsync(oldOrphan.StorageKey));
        Assert.NotNull(await _store.OpenReadAsync(youngOrphan.StorageKey));
        Assert.False(File.Exists(oldPartial));
        foreach (var key in referencedKeys)
        {
            Assert.NotNull(await _store.ComputeSha256Async(key));
        }

        // Idempotent: a second pass finds nothing to do.
        var again = await sweeper.SweepAsync(TimeSpan.FromHours(24));
        Assert.Equal(0, again.OrphansRemoved + again.PartialsRemoved);
        Assert.Equal(documentId, (await _harness.Db.SourceDocuments.SingleAsync()).Id);
    }

    [Fact]
    public void TheLeaseMustOutlastAPage_OrTheWorkerDoesNotStart()
    {
        var bad = Assert.Throws<InvalidOperationException>(() => new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine([("x", 90m)]), new Passthrough(), [new GenericLineTemplateMapper()], _harness.Clock,
            Options.Create(new OcrOptions { WorkerId = "w", LeaseSeconds = 60, PageTimeoutSeconds = 60 }), NullLogger<OcrJobProcessor>.Instance));
        Assert.Contains("LeaseSeconds", bad.Message, StringComparison.Ordinal);

        var badRender = Assert.Throws<InvalidOperationException>(() => TestRendering.Processor(_harness.Db, _store, _harness.Clock,
            new SourceDocumentOptions { RootPath = _root, RenderLeaseSeconds = 30, RenderTimeoutSeconds = 60 }));
        Assert.Contains("RenderLeaseSeconds", badRender.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledArtifactIsVerifiedAgainstItsApprovedHash_BeforeTheBinaryIsExecuted()
    {
        // OCR-017. A stand-in engine file and models with known hashes. With the wrong approved
        // hash, or none, the engine refuses to start with its own category - before any attempt
        // to execute the binary. With every hash right, the next failure is the binary itself
        // (the stand-in cannot run), which proves the order.
        Directory.CreateDirectory(_root);
        var engine = Path.Combine(_root, "tesseract.exe");
        var tessdata = Path.Combine(_root, "tessdata");
        Directory.CreateDirectory(tessdata);
        File.WriteAllBytes(engine, [0x00, 0x01, 0x02]);
        File.WriteAllBytes(Path.Combine(tessdata, "eng.traineddata"), [0x10]);
        File.WriteAllBytes(Path.Combine(tessdata, "osd.traineddata"), [0x20]);
        static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

        OcrOptions Options(Dictionary<string, string>? approved, bool require = true) => new()
        {
            EnginePath = engine, TessdataPath = tessdata, WorkRoot = Path.Combine(_root, "work"),
            ApprovedArtifactHashes = approved ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), RequireApprovedArtifactHashes = require
        };

        var none = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options(null))));
        Assert.Equal(OcrFailureCategory.ArtifactNotApproved, none.Category);

        var wrongEngine = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options(new(StringComparer.OrdinalIgnoreCase)
        {
            ["tesseract.exe"] = new string('0', 64), ["eng.traineddata"] = Hash([0x10]), ["osd.traineddata"] = Hash([0x20])
        }))));
        Assert.Equal(OcrFailureCategory.ArtifactNotApproved, wrongEngine.Category);

        var wrongModel = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options(new(StringComparer.OrdinalIgnoreCase)
        {
            ["tesseract.exe"] = Hash([0x00, 0x01, 0x02]), ["eng.traineddata"] = Hash([0x10]), ["osd.traineddata"] = new string('f', 64)
        }))));
        Assert.Equal(OcrFailureCategory.ArtifactNotApproved, wrongModel.Category);

        var unlisted = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options(new(StringComparer.OrdinalIgnoreCase)
        {
            ["tesseract.exe"] = Hash([0x00, 0x01, 0x02]), ["eng.traineddata"] = Hash([0x10])
        }))));
        Assert.Equal(OcrFailureCategory.ArtifactNotApproved, unlisted.Category);

        var allApproved = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options(new(StringComparer.OrdinalIgnoreCase)
        {
            ["tesseract.exe"] = Hash([0x00, 0x01, 0x02]), ["eng.traineddata"] = Hash([0x10]), ["osd.traineddata"] = Hash([0x20])
        }))));
        Assert.Equal(OcrFailureCategory.EngineUnavailable, allApproved.Category); // the hashes passed; the stand-in cannot execute

        // The message never carries a hash, a path or a file name.
        Assert.DoesNotContain(_root, wrongEngine.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("traineddata", wrongModel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHungBinaryIsKilledDuringTheVersionProbe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // the hanging stand-in is a shell script
        }

        Directory.CreateDirectory(_root);
        var hang = Path.Combine(_root, "tesseract");
        File.WriteAllText(hang, "#!/bin/sh\nsleep 300\n");
        File.SetUnixFileMode(hang, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tessdata = Path.Combine(_root, "tessdata");
        Directory.CreateDirectory(tessdata);
        File.WriteAllBytes(Path.Combine(tessdata, "eng.traineddata"), [0x10]);
        File.WriteAllBytes(Path.Combine(tessdata, "osd.traineddata"), [0x20]);

        var watch = Stopwatch.StartNew();
        var ex = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(new OcrOptions
        {
            EnginePath = hang, TessdataPath = tessdata, WorkRoot = Path.Combine(_root, "work"), RequireApprovedArtifactHashes = false
        })));
        Assert.Equal(OcrFailureCategory.EngineUnavailable, ex.Category);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The hung binary took {watch.Elapsed} to be reported.");

        using var check = Process.Start(new ProcessStartInfo("pgrep", ["-f", hang]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        var survivors = check.StandardOutput.ReadToEnd();
        check.WaitForExit();
        Assert.True(string.IsNullOrWhiteSpace(survivors), "The hung binary survived the version probe.");
    }

    /// <summary>Runs a hook before each page is recognized; reads one fixed word.</summary>
    private sealed class HookedEngine : IOcrEngine
    {
        private readonly Func<Task> _beforeRecognize;
        public HookedEngine(Func<Task> beforeRecognize) => _beforeRecognize = beforeRecognize;
        public string EngineName => "fake";
        public string EngineVersion => "0.0";
        public IReadOnlyList<OcrModelInfo> Models { get; } = [new("eng", new string('0', 64))];
        public Task<OrientationResult> DetectOrientationAsync(byte[] png, CancellationToken ct = default) => Task.FromResult(new OrientationResult(0, 20m));
        public async Task<OcrPageResult> RecognizeAsync(byte[] png, CancellationToken ct = default)
        {
            await _beforeRecognize();
            return new OcrPageResult([new("TEST", 95m, 0, 0, 10, 10, 1, 1, 1, 1)], 100, 100);
        }
    }

    private sealed class Passthrough : IImagePreprocessor
    {
        public string Version => "passthrough/1";
        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default) => new(png, 10, 10, 0, 0, sourceDpi);
    }

    /// <summary>Records OCR page keys and runs a hook after the first one is written.</summary>
    private sealed class InterceptingStore : ISourceDocumentStore
    {
        private readonly ISourceDocumentStore _inner;
        private readonly Func<Task> _onOcrPageWritten;
        private bool _fired;

        public InterceptingStore(ISourceDocumentStore inner, Func<Task> onOcrPageWritten)
        {
            _inner = inner;
            _onOcrPageWritten = onOcrPageWritten;
        }

        public List<string> OcrPageKeys { get; } = [];

        public async Task<StoredBlob> WriteAsync(string category, Stream content, CancellationToken ct = default)
        {
            var blob = await _inner.WriteAsync(category, content, ct);
            if (category == "ocr-pages")
            {
                OcrPageKeys.Add(blob.StorageKey);
                if (!_fired)
                {
                    _fired = true;
                    await _onOcrPageWritten();
                }
            }

            return blob;
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default) => _inner.OpenReadAsync(storageKey, ct);
        public Task<string?> ComputeSha256Async(string storageKey, CancellationToken ct = default) => _inner.ComputeSha256Async(storageKey, ct);
        public Task<bool> TryDeleteAsync(string storageKey, CancellationToken ct = default) => _inner.TryDeleteAsync(storageKey, ct);
        public Task<IReadOnlyList<StoredBlobEntry>> EnumerateAsync(CancellationToken ct = default) => _inner.EnumerateAsync(ct);
        public Task<bool> TryDeletePartialAsync(string storageKey, CancellationToken ct = default) => _inner.TryDeletePartialAsync(storageKey, ct);
    }
}
