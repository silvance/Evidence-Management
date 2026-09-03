using System.Security.Cryptography;
using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Integrity;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Source-document ingestion, immutability, hashing, authorization and integrity.
/// Requirements: DOC-001 .. DOC-011, AUD-022, SEC-013.
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
        => new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock,
            _store, new PdfiumRasterizer(), Options.Create(_options));

    private async Task<int> SubmittedVoucherAsync()
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Doc test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD", "SUBJECT residence", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "One item", "1", null, null, false, false, false, null));
        return voucher.Value;
    }

    private UploadSourceDocumentRequest Request(int voucherId, byte[] bytes, string filename = "scan.pdf", ScanProvenance provenance = ScanProvenance.PhysicalOriginal)
        => new(_harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, provenance, filename, bytes, "UNCLASSIFIED");

    [Fact]
    public async Task AValidPdfIsStoredHashedAndRendered()
    {
        var voucherId = await SubmittedVoucherAsync();
        var bytes = SyntheticPdf.SinglePage();

        var result = await Service().UploadAsync(Request(voucherId, bytes));

        Assert.True(result.Succeeded, result.Error);
        var view = await Service().GetAsync(result.Value);
        Assert.NotNull(view);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), view.Sha256);
        Assert.Equal(bytes.LongLength, view.ContentLength);
        Assert.Equal(1, view.PageCount);
        Assert.Equal(SourceDocumentImportStatus.Rendered, view.ImportStatus);
        Assert.Single(view.Pages);
        Assert.True(view.Pages[0].WidthPx > 100 && view.Pages[0].HeightPx > view.Pages[0].WidthPx);

        // The stored bytes are exactly the upload.
        var stored = await _harness.Db.SourceDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Value);
        Assert.Equal(view.Sha256, await _store.ComputeSha256Async(stored.StorageKey));
        Assert.False(stored.StorageKey.Contains("scan", StringComparison.OrdinalIgnoreCase));

        // A page image is a PNG.
        await using var page = await Service().OpenPageImageAsync(result.Value, 1);
        Assert.NotNull(page);
        var header = new byte[8];
        Assert.Equal(8, await page.ReadAsync(header));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header);
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
    public async Task OversizeAndPathologicalPagesAreRefusedBeforeStorage()
    {
        var voucherId = await SubmittedVoucherAsync();

        var oversize = await Service().UploadAsync(Request(voucherId, new byte[_options.MaxContentBytes + 1]));
        Assert.Equal("DOC-004", oversize.RequirementId);

        var tooMany = await Service().UploadAsync(Request(voucherId, SyntheticPdf.Pages(_options.MaxPageCount + 1)));
        Assert.Equal("DOC-004", tooMany.RequirementId);

        var huge = await Service().UploadAsync(Request(voucherId, SyntheticPdf.PathologicalPage()));
        Assert.Equal("DOC-004", huge.RequirementId);

        Assert.Equal(0, _store.Writes);
        Assert.Empty(_harness.Db.SourceDocuments);
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
    public async Task TheRecordIsImmutable()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        // A fresh tracker, and the document loaded WITHOUT its pages, so that what rejects the
        // delete is the append-only guard and not EF's required-relationship check on the
        // page rows it would otherwise be orphaning.
        _harness.Db.ChangeTracker.Clear();
        var stored = await _harness.Db.SourceDocuments.SingleAsync(d => d.Id == result.Value);

        _harness.Db.Entry(stored).Property(nameof(SourceDocument.Sha256)).CurrentValue = new string('0', 64);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.Entry(stored).State = EntityState.Unchanged;

        _harness.Db.SourceDocuments.Remove(stored);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());

        _harness.Db.ChangeTracker.Clear();
        var page = await _harness.Db.Set<SourceDocumentPage>().FirstAsync(p => p.SourceDocumentId == result.Value);
        _harness.Db.Remove(page);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task OutOfBandMutationIsDetected_AndReportedApartFromChainAndSnapshot()
    {
        // AUD-022. Bytes changed under the key: hash mismatch. A missing file: missing. Neither is
        // an event-chain failure or a snapshot mismatch.
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
        var stored = await _harness.Db.SourceDocuments.AsNoTracking().Include(d => d.Pages).SingleAsync(d => d.Id == result.Value);

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
        Assert.Equal(0, finding.PagesMismatched);

        File.Delete(path);
        report = (await integrity.VerifyEvidenceRoomAsync(_harness.EvidenceRoomId)).Value!;
        Assert.Equal(DocumentHashStatus.Missing, Assert.Single(report.DocumentFindings!).OriginalHash);
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

        _harness.SignInAsAdministrator();
        Assert.Null(await Service().GetAsync(result.Value));
        Assert.Null(await Service().OpenPageImageAsync(result.Value, 1));
        Assert.Null(await Service().OpenOriginalForDownloadAsync(result.Value));

        _harness.CurrentUser.SignIn(_harness.SecondAgentUserId, "SA PATEL, ANIKA R.", _harness.OtherEvidenceRoomId, Emc.Domain.Identity.EmcRoles.Agent);
        Assert.Null(await Service().GetAsync(result.Value));
        Assert.Null(await Service().GetAsync(999_999));
        Assert.Null(await Service().OpenPageImageAsync(result.Value, 1));

        Assert.DoesNotContain(_harness.Db.AuditEvents, a => a.EventType == AuditEventType.SourceDocumentDownloaded);
    }

    [Fact]
    public async Task ThePageEndpointAuthorizesBeforeTouchingTheStore()
    {
        var voucherId = await SubmittedVoucherAsync();
        var result = await Service().UploadAsync(Request(voucherId, SyntheticPdf.SinglePage()));
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
    }
}
