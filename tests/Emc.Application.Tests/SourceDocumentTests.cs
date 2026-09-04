using System.Security.Cryptography;
using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Integrity;
using Emc.Application.Ocr;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Source-document receipt, immutability, hashing, authorization, integrity - and the split
/// between the web service (which never parses a PDF) and the worker (which renders every
/// attempt as an immutable run). Requirements: DOC-001 .. DOC-011, DOC-014, DOC-015, AUD-022, SEC-013.
/// </summary>
public class SourceDocumentTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _options;
    private readonly SpyStore _store;

    public SourceDocumentTests()
    {
        _options = new SourceDocumentOptions { RootPath = _root, MaxPageCount = 20, MaxContentBytes = 2 * 1024 * 1024, RenderDpi = 72 };
        _store = new SpyStore(new FileSystemSourceDocumentStore(Options.Create(_options)));
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ISourceDocumentService Service()
        => new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_options));

    private Task<int> RenderAsync(IPdfRasterizer? rasterizer = null)
        => TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _options, rasterizer);

    private async Task<int> SubmittedVoucherAsync()
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Doc test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SUBJECT residence", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "One item", "1", null, null, false, false, false, null));
        return voucher.Value;
    }

    private UploadSourceDocumentRequest Request(int voucherId, byte[] bytes, string filename = "scan.pdf", ScanProvenance provenance = ScanProvenance.PhysicalOriginal)
        => new(_harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, provenance, filename, bytes, "UNCLASSIFIED");

    [Fact]
    public async Task ReceiptStoresAndHashesAndQueuesARender_TheWorkerRendersThePages()
    {
        // DOC-014: the web service writes the original and a render job and stops. Nothing has
        // parsed the PDF. The worker renders; only then are there pages.
        var voucherId = await SubmittedVoucherAsync();
        var bytes = SyntheticPdf.SinglePage();

        var result = await Service().UploadAsync(Request(voucherId, bytes));
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("rendered by the worker", StringComparison.Ordinal));
        Assert.Equal(1, _store.Writes); // the original only

        var queued = await Service().GetAsync(result.Value);
        Assert.NotNull(queued);
        Assert.Equal(DocumentRenderStatus.Queued, queued.RenderStatus);
        Assert.Null(queued.PageCount);
        Assert.Null(queued.CurrentRenderRunId);
        Assert.Empty(queued.Pages);
        Assert.Single(queued.RenderJobs);
        Assert.Empty(queued.RenderRuns);
        Assert.Null(await Service().OpenPageImageAsync(result.Value, 1));

        Assert.Equal(1, await RenderAsync());

        var view = await Service().GetAsync(result.Value);
        Assert.NotNull(view);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), view.Sha256);
        Assert.Equal(bytes.LongLength, view.ContentLength);
        Assert.Equal(DocumentRenderStatus.Rendered, view.RenderStatus);
        Assert.Equal(1, view.PageCount);
        Assert.NotNull(view.CurrentRenderRunId);
        Assert.Single(view.Pages);
        Assert.True(view.Pages[0].WidthPx > 100 && view.Pages[0].HeightPx > view.Pages[0].WidthPx);
        Assert.Equal(RenderJobStatus.Completed, view.RenderJobs.Single().Status);
        Assert.Equal(RenderRunOutcome.Succeeded, view.RenderRuns.Single().Outcome);

        // The stored bytes are exactly the upload.
        var stored = await _harness.Db.SourceDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Value);
        Assert.Equal(view.Sha256, await _store.ComputeSha256Async(stored.StorageKey));
        Assert.False(stored.StorageKey.Contains("scan", StringComparison.OrdinalIgnoreCase));

        // A page image is a PNG, from the current run.
        await using var page = await Service().OpenPageImageAsync(result.Value, 1);
        Assert.NotNull(page);
        var header = new byte[8];
        Assert.Equal(8, await page.ReadAsync(header));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header);

        var list = await Service().ListForVoucherAsync(voucherId);
        Assert.Equal(DocumentRenderStatus.Rendered, Assert.Single(list).RenderStatus);
        Assert.Equal(1, list[0].PageCount);
    }

    [Fact]
    public async Task ContentDecidesNotTheExtension()
    {
        // DOC-003. A real PDF named ".jpg" is accepted; PNG bytes named ".pdf" are refused.
        var voucherId = await SubmittedVoucherAsync();

        var wrongName = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage("TEST wrong extension"), "scan.jpg"));
        Assert.True(wrongName.Succeeded, wrongName.Error);

        var fake = await Service().UploadAsync(Request(voucherId, SyntheticPdf.FakePdf(), "definitely.pdf"));
        Assert.False(fake.Succeeded);
        Assert.Equal("DOC-003", fake.RequirementId);
    }

    [Fact]
    public async Task OversizeIsRefusedBeforeStorage_AndPageLimitsAreTheWorkersFinalFailures()
    {
        // DOC-004 at the web layer: the size. Page count and page size are only knowable by
        // parsing, which the web process never does; the worker refuses them as final failures,
        // before any page is rendered, and the document keeps its record and its download.
        var voucherId = await SubmittedVoucherAsync();

        var oversize = await Service().UploadAsync(Request(voucherId, new byte[_options.MaxContentBytes + 1]));
        Assert.Equal("DOC-004", oversize.RequirementId);
        Assert.Equal(0, _store.Writes);
        Assert.Empty(_harness.Db.SourceDocuments);

        var tooMany = await Service().UploadAsync(Request(voucherId, SyntheticPdf.Pages(_options.MaxPageCount + 1)));
        Assert.True(tooMany.Succeeded, tooMany.Error);
        var huge = await Service().UploadAsync(Request(voucherId, SyntheticPdf.PathologicalPage()));
        Assert.True(huge.Succeeded, huge.Error);
        Assert.Equal(2, _store.Writes);

        Assert.Equal(2, await RenderAsync());
        Assert.Equal(2, _store.Writes); // no page blob was written for either

        foreach (var id in new[] { tooMany.Value, huge.Value })
        {
            var view = (await Service().GetAsync(id))!;
            Assert.Equal(DocumentRenderStatus.Failed, view.RenderStatus);
            Assert.Equal(RenderFailureCategory.ResourceLimitExceeded, view.LastRenderFailure);
            Assert.Equal(RenderJobStatus.Failed, view.RenderJobs.Single().Status);
            Assert.Equal(1, view.RenderJobs.Single().Attempts); // not transient: final on the first attempt
            var run = Assert.Single(view.RenderRuns);
            Assert.Equal(RenderRunOutcome.Failed, run.Outcome);
            Assert.Empty(view.Pages);
        }

        Assert.Equal(_options.MaxPageCount + 1, (await Service().GetAsync(tooMany.Value))!.RenderRuns.Single().PageCount);
    }

    [Fact]
    public async Task ATransientRenderFailureIsRetried_EveryAttemptStaysOnRecord_AndTheNewestSuccessIsCurrent()
    {
        // DOC-015. Attempt 1 crashes (transient): the job requeues. Attempt 2 succeeds. A person
        // then asks for a fresh render: a new job, a third run; the current page set is the newest
        // successful run. Three immutable rows; nothing overwritten.
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        Assert.True(result.Succeeded, result.Error);

        // One attempt only: a requeued job would otherwise be taken again by the same loop.
        Assert.True(await TestRendering.Processor(_harness.Db, _store, _harness.Clock, _options, new FlakyRasterizer(failuresBeforeSuccess: 1)).ProcessNextAsync());
        var afterCrash = (await Service().GetAsync(result.Value))!;
        Assert.Equal(DocumentRenderStatus.Queued, afterCrash.RenderStatus);
        Assert.Equal(RenderFailureCategory.RendererCrashed, afterCrash.LastRenderFailure);
        Assert.Equal(RenderJobStatus.Queued, afterCrash.RenderJobs.Single().Status);
        Assert.Equal(1, afterCrash.RenderJobs.Single().Attempts);
        Assert.Equal(RenderRunOutcome.Failed, afterCrash.RenderRuns.Single().Outcome);
        Assert.Equal(1, _store.Writes);

        Assert.Equal(1, await RenderAsync());
        var rendered = (await Service().GetAsync(result.Value))!;
        Assert.Equal(DocumentRenderStatus.Rendered, rendered.RenderStatus);
        Assert.Equal(2, rendered.RenderRuns.Count);
        var firstSuccess = rendered.CurrentRenderRunId!.Value;
        Assert.Equal(RenderRunOutcome.Succeeded, rendered.RenderRuns.Single(r => r.RunId == firstSuccess).Outcome);

        // A fresh render on request; refused while one is open.
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = await Service().RequestRenderAsync(result.Value);
        Assert.True(again.Succeeded, again.Error);
        var duplicate = await Service().RequestRenderAsync(result.Value);
        Assert.False(duplicate.Succeeded);
        Assert.Equal("DOC-014", duplicate.RequirementId);
        var pending = (await Service().GetAsync(result.Value))!;
        Assert.Equal(DocumentRenderStatus.Rendered, pending.RenderStatus); // the earlier pages stay available
        Assert.True(pending.HasOpenRenderJob);
        Assert.Equal(firstSuccess, pending.CurrentRenderRunId);

        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await RenderAsync());
        var latest = (await Service().GetAsync(result.Value))!;
        Assert.Equal(3, latest.RenderRuns.Count);
        Assert.Equal(2, latest.RenderJobs.Count);
        Assert.NotEqual(firstSuccess, latest.CurrentRenderRunId);
        Assert.False(latest.HasOpenRenderJob);

        // Every run is still there, unchanged, and the earlier success's page bytes were never removed.
        var runs = await _harness.Db.DocumentRenderRuns.AsNoTracking().Include(r => r.Pages).Where(r => r.SourceDocumentId == result.Value).ToListAsync();
        Assert.Equal(3, runs.Count);
        Assert.Equal(2, runs.Count(r => r.Outcome == RenderRunOutcome.Succeeded));
        foreach (var page in runs.Where(r => r.Outcome == RenderRunOutcome.Succeeded).SelectMany(r => r.Pages))
        {
            Assert.Equal(page.Sha256, await _store.ComputeSha256Async(page.StorageKey));
        }

        // An OCR request binds to the current run at request time (DOC-015).
        var ocr = new OcrJobService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store);
        var ocrRequest = await ocr.RequestAsync(result.Value);
        Assert.True(ocrRequest.Succeeded, ocrRequest.Error);
        var ocrJob = await _harness.Db.OcrJobs.AsNoTracking().SingleAsync(j => j.Id == ocrRequest.Value);
        Assert.Equal(latest.CurrentRenderRunId, ocrJob.RenderRunId);
    }

    [Fact]
    public async Task AMalformedPdfIsAFinalFailure_AndOcrCannotBeRequestedForIt()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        Assert.True(result.Succeeded, result.Error);

        Assert.Equal(1, await RenderAsync(new FlakyRasterizer(malformed: true)));
        var view = (await Service().GetAsync(result.Value))!;
        Assert.Equal(DocumentRenderStatus.Failed, view.RenderStatus);
        Assert.Equal(RenderFailureCategory.MalformedPdf, view.LastRenderFailure);
        Assert.Equal(1, view.RenderJobs.Single().Attempts);

        var ocr = new OcrJobService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store);
        var refused = await ocr.RequestAsync(result.Value);
        Assert.False(refused.Succeeded);
        Assert.Equal("OCR-010", refused.RequirementId);

        // The original is still downloadable by a permitted user: the record is intact.
        _harness.SignInAsCustodian();
        await using var pdf = await Service().OpenOriginalForDownloadAsync(result.Value);
        Assert.NotNull(pdf);
    }

    [Fact]
    public async Task TheOriginalFilenameCannotEscapeTheStoreRoot()
    {
        // DOC-006. The name is metadata; the key is generated; the store re-validates keys.
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage(), "..\\..\\..\\windows\\system32\\evil.pdf"));

        Assert.True(result.Succeeded, result.Error);
        var stored = await _harness.Db.SourceDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Value);
        Assert.StartsWith("documents/", stored.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain("..", stored.StorageKey, StringComparison.Ordinal);
        Assert.Contains("evil.pdf", stored.OriginalFilename, StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => _store.Inner.OpenReadAsync("../outside.bin").GetAwaiter().GetResult());
        Assert.Throws<DomainRuleViolationException>(() => SourceDocument.ValidateStorageKey("documents/../../x"));
        Assert.Throws<DomainRuleViolationException>(() => SourceDocument.ValidateStorageKey("C:/x"));
    }

    [Fact]
    public async Task TheRecordIsImmutable_AndSoIsEveryRenderAttempt()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        await RenderAsync();

        _harness.Db.ChangeTracker.Clear();
        var stored = await _harness.Db.SourceDocuments.SingleAsync(d => d.Id == result.Value);

        _harness.Db.Entry(stored).Property(nameof(SourceDocument.Sha256)).CurrentValue = new string('0', 64);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.Entry(stored).State = EntityState.Unchanged;

        _harness.Db.SourceDocuments.Remove(stored);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());

        // The render run and its page: append-only (DOC-015).
        _harness.Db.ChangeTracker.Clear();
        var run = await _harness.Db.DocumentRenderRuns.SingleAsync(r => r.SourceDocumentId == result.Value);
        _harness.Db.Entry(run).Property(nameof(DocumentRenderRun.RendererVersion)).CurrentValue = "tampered";
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());

        _harness.Db.ChangeTracker.Clear();
        var page = await _harness.Db.Set<DocumentRenderPage>().FirstAsync(p => p.RenderRunId == run.Id);
        _harness.Db.Remove(page);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());

        // The render JOB is a work record: leasing and settling it is how the worker works.
        _harness.Db.ChangeTracker.Clear();
        Assert.False(typeof(IAppendOnly).IsAssignableFrom(typeof(DocumentRenderJob)));
        Assert.True(typeof(IConcurrencyStamped).IsAssignableFrom(typeof(DocumentRenderJob)));
    }

    [Fact]
    public async Task OutOfBandMutationIsDetected_AndReportedApartFromChainAndSnapshot()
    {
        // AUD-022. Bytes changed under the key: hash mismatch. A missing file: missing. Neither is
        // an event-chain failure or a snapshot mismatch. Rendered pages are checked on every
        // successful run.
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        await RenderAsync();
        var stored = await _harness.Db.SourceDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Value);

        var path = Path.Combine(_root, stored.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(path, "tampered");

        _harness.SignInAsAdministrator();
        var integrity = new IntegrityVerificationService(_harness.Db, _harness.Authorization, _harness.Audit, _harness.Clock, _store);
        var report = (await integrity.VerifyEvidenceRoomAsync(_harness.EvidenceRoomId)).Value!;

        Assert.Equal(1, report.DocumentsChecked);
        Assert.Equal(1, report.DocumentIntegrityFailures);
        Assert.Equal(0, report.EventChainFailures);
        Assert.Equal(0, report.SnapshotMismatches);
        var finding = Assert.Single(report.DocumentFindings!);
        Assert.Equal(DocumentHashStatus.Mismatch, finding.OriginalHash);
        Assert.Equal(1, finding.PagesChecked);
        Assert.Equal(0, finding.PagesMismatched);

        File.Delete(path);
        var page = await _harness.Db.Set<DocumentRenderPage>().AsNoTracking().SingleAsync(p => p.Run!.SourceDocumentId == result.Value);
        await File.AppendAllTextAsync(Path.Combine(_root, page.StorageKey.Replace('/', Path.DirectorySeparatorChar)), "tampered");
        report = (await integrity.VerifyEvidenceRoomAsync(_harness.EvidenceRoomId)).Value!;
        finding = Assert.Single(report.DocumentFindings!);
        Assert.Equal(DocumentHashStatus.Missing, finding.OriginalHash);
        Assert.Equal(1, finding.PagesMismatched);
    }

    [Fact]
    public void TheIntegrityRowCarriesNoContent()
    {
        var props = typeof(DocumentIntegrityRow).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "DocumentId", "EvidenceRoomId", "OriginalHash", "PagesChecked", "PagesMismatched" }, props);
    }

    [Fact]
    public async Task TheAdministratorCannotViewOrDownload_AndAnotherRoomCannotProbe()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        await RenderAsync();

        _harness.SignInAsAdministrator();
        Assert.Null(await Service().GetAsync(result.Value));
        Assert.Null(await Service().OpenPageImageAsync(result.Value, 1));
        Assert.Null(await Service().OpenOriginalForDownloadAsync(result.Value));
        Assert.False((await Service().RequestRenderAsync(result.Value)).Succeeded);

        _harness.CurrentUser.SignIn(_harness.SecondAgentUserId, "SA PATEL, ANIKA R.", _harness.OtherEvidenceRoomId, Emc.Domain.Identity.EmcRoles.Agent);
        Assert.Null(await Service().GetAsync(result.Value));
        Assert.Null(await Service().GetAsync(999_999));
        Assert.Null(await Service().OpenPageImageAsync(result.Value, 1));
        Assert.Equal("The document was not found.", (await Service().RequestRenderAsync(result.Value)).Error);

        Assert.DoesNotContain(_harness.Db.AuditEvents, a => a.EventType == AuditEventType.SourceDocumentDownloaded);
    }

    [Fact]
    public async Task ThePageEndpointAuthorizesBeforeTouchingTheStore()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        await RenderAsync();
        _store.Reads = 0;

        _harness.SignInAsAdministrator();
        Assert.Null(await Service().OpenPageImageAsync(result.Value, 1));
        Assert.Null(await Service().OpenOriginalForDownloadAsync(result.Value));
        Assert.Equal(0, _store.Reads);

        _harness.SignInAsAgent();
        await using var page = await Service().OpenPageImageAsync(result.Value, 1);
        Assert.NotNull(page);
        Assert.Equal(1, _store.Reads);
    }

    [Fact]
    public async Task DownloadNeedsItsOwnPermissionAndIsAudited()
    {
        // DOC-009. The agent may view but not download; the custodian may download, and it is logged.
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));

        _harness.SignInAsAgent();
        Assert.NotNull(await Service().GetAsync(result.Value));
        Assert.Null(await Service().OpenOriginalForDownloadAsync(result.Value));

        _harness.SignInAsCustodian();
        await using var pdf = await Service().OpenOriginalForDownloadAsync(result.Value);
        Assert.NotNull(pdf);

        var audit = Assert.Single(_harness.Db.AuditEvents.Where(a => a.EventType == AuditEventType.SourceDocumentDownloaded));
        Assert.Equal(result.Value.ToString(), audit.AffectedRecordId);
        Assert.DoesNotContain("TEST-CI", audit.NewValue ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedRequestIsRefused_ButTheSameScanElsewhereIsKeptWithAWarning()
    {
        // DOC-010 / DOC-011.
        var voucherId = await SubmittedVoucherAsync();
        var bytes = SyntheticPdf.SinglePage();

        var first = await Service().UploadAsync(Request(voucherId, bytes));
        Assert.True(first.Succeeded, first.Error);

        var repeated = await Service().UploadAsync(Request(voucherId, bytes));
        Assert.False(repeated.Succeeded);
        Assert.Equal("DOC-010", repeated.RequirementId);

        var otherVoucher = await SubmittedVoucherAsync();
        var elsewhere = await Service().UploadAsync(Request(otherVoucher, bytes));
        Assert.True(elsewhere.Succeeded, elsewhere.Error);
        Assert.Contains(elsewhere.Warnings, w => w.Contains("identical content", StringComparison.Ordinal));
        Assert.NotEqual(first.Value, elsewhere.Value);
    }

    [Fact]
    public async Task AMarkingAboveTheAccreditedLevelIsRefused()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(new UploadSourceDocumentRequest(
            _harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalCopy, "scan.pdf",
            SyntheticPdf.SinglePage(), "SECRET"));

        Assert.False(result.Succeeded);
        Assert.Equal("SEC-003", result.RequirementId);
    }

    [Fact]
    public async Task ActiveContentIsReportedNeverExecuted()
    {
        var voucherId = await SubmittedVoucherAsync();
        var bytes = SyntheticPdf.SinglePage();
        // Splice an action dictionary token into the trailer region; PDFium still opens the file.
        var withJs = bytes.Concat("\n% /OpenAction /JavaScript (app.alert(1))\n%%EOF\n"u8.ToArray()).ToArray();

        var result = await Service().UploadAsync(Request(voucherId, withJs));

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("never executed", StringComparison.Ordinal));
        Assert.Equal(1, await RenderAsync());
        Assert.Equal(DocumentRenderStatus.Rendered, (await Service().GetAsync(result.Value))!.RenderStatus);
    }

    [Fact]
    public void TheValidatorIsStructural()
    {
        Assert.True(PdfContentValidator.Validate(SyntheticPdf.SinglePage(), 10_000_000).IsValid);
        Assert.Equal("DOC-003", PdfContentValidator.Validate(SyntheticPdf.FakePdf(), 10_000_000).RequirementId);
        Assert.Equal("DOC-003", PdfContentValidator.Validate([], 10).RequirementId);
        Assert.Equal("DOC-004", PdfContentValidator.Validate(SyntheticPdf.SinglePage(), 10).RequirementId);
        Assert.Equal("DOC-003", PdfContentValidator.Validate("%PDF-1.7 truncated"u8, 10_000).RequirementId);
    }

    [Fact]
    public void TheWebServiceHasNoRasterizerToCall()
    {
        // DOC-014, by construction: nothing in the receipt service's dependencies can parse a PDF.
        var parameters = typeof(SourceDocumentService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.DoesNotContain(typeof(IPdfRasterizer), parameters);
        Assert.Null(typeof(SourceDocumentService).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(f => f.FieldType == typeof(IPdfRasterizer)));
    }

    /// <summary>A rasterizer that crashes a set number of times, or reports every document malformed.</summary>
    private sealed class FlakyRasterizer : IPdfRasterizer
    {
        private readonly PdfiumRasterizer _inner = new();
        private readonly bool _malformed;
        private int _failuresLeft;

        public FlakyRasterizer(int failuresBeforeSuccess = 0, bool malformed = false)
        {
            _failuresLeft = failuresBeforeSuccess;
            _malformed = malformed;
        }

        public string RendererVersion => "flaky/1";

        public int GetPageCount(byte[] pdf)
        {
            if (_malformed)
            {
                throw new MalformedPdfException("not a pdf");
            }

            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new RendererCrashedException("the child died");
            }

            return _inner.GetPageCount(pdf);
        }

        public IReadOnlyList<PdfPageDimensions> GetPageDimensions(byte[] pdf) => _inner.GetPageDimensions(pdf);
        public RenderedPage Render(byte[] pdf, int pageNumber, int dpi, CancellationToken ct = default) => _inner.Render(pdf, pageNumber, dpi, ct);
    }

    /// <summary>Counts store calls so a test can prove authorization happened before any read.</summary>
    private sealed class SpyStore : ISourceDocumentStore
    {
        public SpyStore(ISourceDocumentStore inner) => Inner = inner;
        public ISourceDocumentStore Inner { get; }
        public int Reads { get; set; }
        public int Writes { get; private set; }

        public Task<StoredBlob> WriteAsync(string category, Stream content, CancellationToken ct = default)
        { Writes++; return Inner.WriteAsync(category, content, ct); }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
        { Reads++; return Inner.OpenReadAsync(storageKey, ct); }

        public Task<string?> ComputeSha256Async(string storageKey, CancellationToken ct = default)
            => Inner.ComputeSha256Async(storageKey, ct);

        public Task<bool> TryDeleteAsync(string storageKey, CancellationToken ct = default)
            => Inner.TryDeleteAsync(storageKey, ct);

        public Task<IReadOnlyList<StoredBlobEntry>> EnumerateAsync(CancellationToken ct = default)
            => Inner.EnumerateAsync(ct);

        public Task<bool> TryDeletePartialAsync(string storageKey, CancellationToken ct = default)
            => Inner.TryDeletePartialAsync(storageKey, ct);
    }
}
