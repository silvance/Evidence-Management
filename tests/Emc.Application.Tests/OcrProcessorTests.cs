using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Domain.Ocr;
using Emc.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The OCR job pipeline with a FAKE engine: request, lease, run, fields, verification, failure
/// categories, concurrency between two workers, and authorization. The real engine is exercised
/// in TesseractEngineTests. Requirements: OCR-001, OCR-002, OCR-003, OCR-004, OCR-006, OCR-010 ..
/// OCR-014, SEC-014.
/// </summary>
public class OcrProcessorTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _docOptions;
    private readonly FileSystemSourceDocumentStore _store;

    public OcrProcessorTests()
    {
        _docOptions = new SourceDocumentOptions { RootPath = _root, RenderDpi = 72 };
        _store = new FileSystemSourceDocumentStore(Options.Create(_docOptions));
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private IOcrJobService Jobs() => new OcrJobService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store);

    private OcrJobProcessor Processor(IOcrEngine engine, string workerId, Emc.Infrastructure.Persistence.EmcDbContext? db = null, OcrOptions? options = null)
        => new(db ?? _harness.Db, _store, engine, new PassThroughPreprocessor(), [new GenericLineTemplateMapper()], _harness.Clock,
               Options.Create(options ?? new OcrOptions { WorkerId = workerId, MaxAttempts = 3, LeaseSeconds = 600 }), NullLogger<OcrJobProcessor>.Instance);

    private async Task<int> UploadedDocumentAsync(int pages = 1)
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "OCR test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD", "SUBJECT residence", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "One item", "1", null, null, false, false, false, null));

        var documents = new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock,
            _store, Options.Create(_docOptions));
        var result = await documents.UploadAsync(new UploadSourceDocumentRequest(
            _harness.EvidenceRoomId, null, voucher.Value, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.Pages(pages), "UNCLASSIFIED"));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions));
        return result.Value;
    }

    [Fact]
    public async Task RequestRunVerify_EndToEnd_WithTheRawTextKept()
    {
        var documentId = await UploadedDocumentAsync();

        var request = await Jobs().RequestAsync(documentId);
        Assert.True(request.Succeeded, request.Error);
        Assert.Contains(request.Warnings, w => w.Contains("remains authoritative", StringComparison.Ordinal));

        // A second request while one is open is refused, not duplicated.
        var again = await Jobs().RequestAsync(documentId);
        Assert.False(again.Succeeded);
        Assert.Equal("OCR-010", again.RequirementId);

        var engine = new FakeEngine([("TEST", 96m), ("DA", 95m), ("FORM", 97m), ("4137", 93m)], [("faint", 70m)]);
        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());
        Assert.False(await Processor(engine, "worker-a").ProcessNextAsync()); // queue empty

        var status = await Jobs().GetStatusAsync(documentId);
        Assert.NotNull(status);
        Assert.Single(status.Jobs);
        Assert.Equal(OcrJobStatus.Completed, status.Jobs[0].Status);
        Assert.NotNull(status.LatestRun);
        var run = status.LatestRun!;
        Assert.Equal(OcrRunOutcome.Succeeded, run.Outcome);
        Assert.Equal("fake", run.EngineName);
        Assert.Contains("eng@sha256:", run.ModelIdentifiers, StringComparison.Ordinal);
        Assert.Equal(GenericLineTemplateMapper.Id, run.TemplateId);
        Assert.False(run.TemplateIdentified);
        Assert.Equal(2, run.Fields.Count);
        Assert.Single(run.Pages);
        await using (var runImage = await Jobs().OpenRunPageImageAsync(run.RunId, 1))
        {
            Assert.NotNull(runImage);
        }

        var line1 = run.Fields.Single(f => f.FieldKey == "Page[1].Line[1]");
        Assert.Equal("TEST DA FORM 4137", line1.RawText);
        Assert.Equal(ConfidenceBand.High, line1.Band);
        Assert.False(line1.RequiresVerification); // low-consequence, high confidence: reviewable, not mandatory
        var line2 = run.Fields.Single(f => f.FieldKey == "Page[1].Line[2]");
        Assert.Equal(ConfidenceBand.Medium, line2.Band);
        Assert.True(line2.RequiresVerification);
        Assert.Equal(1, run.MandatoryOutstanding);

        // Verify: a correction keeps the raw text.
        var verify = await Jobs().VerifyFieldAsync(new VerifyFieldRequest(line2.FieldId, FieldVerificationDecision.CorrectedByVerifier, "FAINT", "Read from the scan"));
        Assert.True(verify.Succeeded, verify.Error);

        status = await Jobs().GetStatusAsync(documentId);
        var verified = status!.LatestRun!.Fields.Single(f => f.FieldId == line2.FieldId);
        Assert.Equal("faint", verified.RawText);
        Assert.Equal("FAINT", verified.VerifiedValue);
        Assert.Equal(_harness.AgentPrintedNameAndGrade, verified.Current!.VerifiedByName);
        Assert.True(status.LatestRun.VerificationComplete);

        // The run row and its fields cannot be changed through the context (layer 2).
        _harness.Db.ChangeTracker.Clear();
        var stored = await _harness.Db.OcrRuns.SingleAsync(r => r.Id == run.RunId);
        _harness.Db.Entry(stored).Property(nameof(OcrRun.EngineVersion)).CurrentValue = "tampered";
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task ATimeoutIsATransientFailure_RequeuedThenFinal_WithNoTextAnywhere()
    {
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);
        var engine = new FakeEngine(throwOnRecognize: new OperationCanceledException());

        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());
        var job = await _harness.Db.OcrJobs.AsNoTracking().SingleAsync(j => j.SourceDocumentId == documentId);
        Assert.Equal(OcrJobStatus.Queued, job.Status);
        Assert.Equal(OcrFailureCategory.Timeout, job.LastFailureCategory);

        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());
        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());
        job = await _harness.Db.OcrJobs.AsNoTracking().SingleAsync(j => j.SourceDocumentId == documentId);
        Assert.Equal(OcrJobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempts);

        var runs = await _harness.Db.OcrRuns.AsNoTracking().Include(r => r.Fields).Where(r => r.SourceDocumentId == documentId).ToListAsync();
        Assert.Equal(3, runs.Count);
        Assert.All(runs, r => { Assert.Equal(OcrRunOutcome.Failed, r.Outcome); Assert.Equal(OcrFailureCategory.Timeout, r.FailureCategory); Assert.Empty(r.Fields); });

        // A new request may be made once the job is closed.
        _harness.SignInAsAgent();
        Assert.True((await Jobs().RequestAsync(documentId)).Succeeded);
    }

    [Fact]
    public async Task AnEngineFailureCategoryIsRecorded_ModelMissingIsFinal()
    {
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);
        var engine = new FakeEngine(throwOnRecognize: new OcrEngineException(OcrFailureCategory.ModelMissing));

        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());
        var job = await _harness.Db.OcrJobs.AsNoTracking().SingleAsync(j => j.SourceDocumentId == documentId);
        Assert.Equal(OcrJobStatus.Failed, job.Status);
        Assert.Equal(OcrFailureCategory.ModelMissing, job.LastFailureCategory);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task AMissingPageImageIsDocumentUnavailable_NotACrash()
    {
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);
        var page = await _harness.Db.Set<DocumentRenderPage>().AsNoTracking().SingleAsync(p => p.Run!.SourceDocumentId == documentId);
        File.Delete(Path.Combine(_root, page.StorageKey.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(await Processor(new FakeEngine([("x", 90m)]), "worker-a").ProcessNextAsync());
        var run = await _harness.Db.OcrRuns.AsNoTracking().SingleAsync(r => r.SourceDocumentId == documentId);
        Assert.Equal(OcrFailureCategory.DocumentUnavailable, run.FailureCategory);
    }

    [Fact]
    public async Task TwoWorkersCannotLeaseTheSameJob()
    {
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);

        // Worker B leases through a second context after worker A has already leased through the first.
        using var second = _harness.CreateSecondContext();
        var jobA = await _harness.Db.OcrJobs.SingleAsync(j => j.SourceDocumentId == documentId);
        var jobB = await second.OcrJobs.SingleAsync(j => j.SourceDocumentId == documentId);

        jobA.Lease("worker-a", _harness.Clock.UtcNow, TimeSpan.FromMinutes(10));
        await _harness.Db.SaveChangesAsync();

        jobB.Lease("worker-b", _harness.Clock.UtcNow, TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        // And the processor, meeting a job somebody else just took, simply finds nothing.
        Assert.False(await Processor(new FakeEngine([("x", 90m)]), "worker-b", second).ProcessNextAsync());
    }

    [Fact]
    public async Task AnExpiredLeaseIsTakenOver()
    {
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);
        var job = await _harness.Db.OcrJobs.SingleAsync(j => j.SourceDocumentId == documentId);
        job.Lease("worker-dead", _harness.Clock.UtcNow, TimeSpan.FromMinutes(10));
        await _harness.Db.SaveChangesAsync();
        _harness.Db.ChangeTracker.Clear();

        Assert.False(await Processor(new FakeEngine([("x", 90m)]), "worker-b").ProcessNextAsync());
        _harness.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await Processor(new FakeEngine([("x", 90m)]), "worker-b").ProcessNextAsync());

        var run = await _harness.Db.OcrRuns.AsNoTracking().SingleAsync(r => r.SourceDocumentId == documentId);
        Assert.Equal("worker-b", run.WorkerId);
    }

    [Fact]
    public async Task OrientationIsVotedWhenTheEngineIsUnsure()
    {
        // OSD says "no idea"; the engine reads well only at 180. The run records that orientation.
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);
        var engine = new FakeEngine([("UPRIGHT", 95m), ("TEXT", 94m), ("HERE", 96m), ("NOW", 93m)], goodAtRotation: 180, osdConfidence: 0.5m);

        Assert.True(await Processor(engine, "worker-a").ProcessNextAsync());
        var run = await _harness.Db.OcrRuns.AsNoTracking().Include(r => r.Fields).SingleAsync(r => r.SourceDocumentId == documentId);
        Assert.Equal(OcrRunOutcome.Succeeded, run.Outcome);
        Assert.Equal("UPRIGHT TEXT HERE NOW", run.Fields.Single().RawText);
        Assert.Contains(180, engine.RotationsSeen);
    }

    [Fact]
    public async Task OcrIsRoomScoped_AndVerificationNeedsItsPermission()
    {
        var documentId = await UploadedDocumentAsync();
        await Jobs().RequestAsync(documentId);
        await Processor(new FakeEngine([("faint", 70m)]), "worker-a").ProcessNextAsync();
        var fieldId = (await _harness.Db.OcrRuns.AsNoTracking().Include(r => r.Fields).SingleAsync(r => r.SourceDocumentId == documentId)).Fields.Single().Id;

        // An agent of another room only: the document, its status and its fields do not exist.
        var outsider = new Emc.Domain.Identity.User("S-1-5-21-OCR-OUTSIDER", "outsider.ocr@army.mil", "FOX, JAMIE R.");
        outsider.UpdateProfile("FOX, JAMIE R.", "SA", "310th MI Bn");
        _harness.Db.Users.Add(outsider);
        await _harness.Db.SaveChangesAsync();
        _harness.GrantRoleInRoom(outsider.Id, Emc.Domain.Identity.EmcRoles.Agent, _harness.OtherEvidenceRoomId);
        await _harness.Db.SaveChangesAsync();
        _harness.CurrentUser.SignIn(outsider.Id, "SA FOX, JAMIE R.", _harness.OtherEvidenceRoomId, Emc.Domain.Identity.EmcRoles.Agent);
        Assert.Null(await Jobs().GetStatusAsync(documentId));
        var request = await Jobs().RequestAsync(documentId);
        Assert.False(request.Succeeded);
        Assert.Equal("The document was not found.", request.Error);
        var verify = await Jobs().VerifyFieldAsync(new VerifyFieldRequest(fieldId, FieldVerificationDecision.CorrectedByVerifier, "X", null));
        Assert.False(verify.Succeeded);
        Assert.Equal("The field was not found.", verify.Error);

        // The administrator holds no evidence permission at all (IAM-009).
        _harness.SignInAsAdministrator();
        Assert.Null(await Jobs().GetStatusAsync(documentId));
        Assert.False((await Jobs().RequestAsync(documentId)).Succeeded);
        Assert.False((await Jobs().VerifyFieldAsync(new VerifyFieldRequest(fieldId, FieldVerificationDecision.NotApplicable, null, null))).Succeeded);
    }

    /// <summary>
    /// A preprocessor that does no image work: its "image" is a single byte naming the rotation
    /// it was asked for, which is all the fake engine looks at.
    /// </summary>
    private sealed class PassThroughPreprocessor : IImagePreprocessor
    {
        public string Version => "passthrough/1";

        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default)
            => new([(byte)(rotateClockwiseDegrees / 90)], 100, 100, rotateClockwiseDegrees, 0, sourceDpi);
    }

    internal sealed class FakeEngine : IOcrEngine
    {
        private readonly IReadOnlyList<(string Text, decimal Confidence)>[] _lines;
        private readonly Exception? _throwOnRecognize;
        private readonly int _goodAtRotation;
        private readonly decimal _osdConfidence;

        public FakeEngine(IReadOnlyList<(string, decimal)> line1, IReadOnlyList<(string, decimal)>? line2 = null, int goodAtRotation = 0, decimal osdConfidence = 20m)
        {
            _lines = line2 is null ? [line1] : [line1, line2];
            _goodAtRotation = goodAtRotation;
            _osdConfidence = osdConfidence;
        }

        public FakeEngine(Exception throwOnRecognize)
        {
            _lines = [];
            _throwOnRecognize = throwOnRecognize;
        }

        public List<int> RotationsSeen { get; } = [];

        public string EngineName => "fake";
        public string EngineVersion => "0.0";
        public IReadOnlyList<OcrModelInfo> Models { get; } = [new("eng", new string('0', 64))];

        public Task<OrientationResult> DetectOrientationAsync(byte[] png, CancellationToken ct = default)
            => Task.FromResult(new OrientationResult(_goodAtRotation, _osdConfidence));

        public Task<OcrPageResult> RecognizeAsync(byte[] png, CancellationToken ct = default)
        {
            if (_throwOnRecognize is not null)
            {
                throw _throwOnRecognize;
            }

            var rotation = png.Length == 1 ? png[0] * 90 : 0;
            RotationsSeen.Add(rotation);
            var good = rotation == _goodAtRotation;
            var words = new List<OcrWord>();
            for (var l = 0; l < _lines.Length; l++)
            {
                for (var w = 0; w < _lines[l].Count; w++)
                {
                    var (text, conf) = _lines[l][w];
                    words.Add(new OcrWord(good ? text : "~", good ? conf : 20m, 10 + w * 50, 10 + l * 30, 40, 20, 1, 1, l + 1, w + 1));
                }
            }

            return Task.FromResult(new OcrPageResult(words, 100, 100));
        }
    }
}
