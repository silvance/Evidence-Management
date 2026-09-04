using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Items;
using Emc.Application.Ocr;
using Emc.Application.Reconciliation;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Domain.Events;
using Emc.Domain.Ocr;
using Emc.Domain.Reconciliation;
using Emc.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// A custody row the verified scan shows and the companion lacks reaches the item's chain only
/// by an appointed custodian's explicit act through the custody workflow, with the paper's date
/// as OccurredAt, now as RecordedAt, the scan as provenance and the finding as correlation; no
/// 1-7c(3) MFR for a mere backfill; no release and no status change. Requirements: REC-003, REC-010, COC-002.
/// </summary>
public class ReconciliationCustodyTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _docOptions;
    private readonly FileSystemSourceDocumentStore _store;

    public ReconciliationCustodyTests()
    {
        _docOptions = new SourceDocumentOptions { RootPath = _root, RenderDpi = 72 };
        _store = new FileSystemSourceDocumentStore(Options.Create(_docOptions));
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private IOcrJobService Ocr() => new OcrJobService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store);
    private IReconciliationService Reconciliation() => new ReconciliationService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, Ocr(), _harness.Reads, _harness.Vouchers);

    private async Task<(int VoucherId, int ItemId, int DocumentId, int FindingId)> AcceptedVoucherWithACustodyRowFindingAsync()
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Custody backfill", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A.", _harness.Clock.UtcNow.AddDays(-3), false, null));
        var item = await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "ONE TEST MOBILE TELEPHONE, BLACK", "1", "TESTSERIAL000001", null, false, false, false, null));
        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucher.Value);
        _harness.SignInAsCustodian();
        Assert.True((await _harness.Intake.RecordOfficialDocumentNumberAsync(new RecordDocumentNumberRequest(voucher.Value, "011-26", true, _harness.Clock.UtcNow))).Succeeded);

        _harness.SignInAsAgent();
        var documents = new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_docOptions));
        var upload = await documents.UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucher.Value, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        Assert.True(upload.Succeeded, upload.Error);
        await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions);
        Assert.True((await Ocr().RequestAsync(upload.Value)).Succeeded);
        var processor = new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine([("x", 95m)]), new Passthrough(),
            [new FixedMapper([("Item[1].ItemNumber", "1"), ("Item[1].Description", "ONE TEST MOBILE TELEPHONE, BLACK"),
                ("Custody[1].ItemNumber", "1"), ("Custody[1].Date", "01 SEP 26"), ("Custody[1].ReleasedByName", "SMITH, TEST A."), ("Custody[1].ReceivedByName", "JONES, TEST D."), ("Custody[1].Purpose", "HAND RECEIPT TO CASE AGENT")])],
            _harness.Clock, Options.Create(new OcrOptions { WorkerId = "w" }), NullLogger<OcrJobProcessor>.Instance);
        Assert.True(await processor.ProcessNextAsync());
        var status = await Ocr().GetStatusAsync(upload.Value);
        foreach (var f in status!.LatestRun!.Fields)
        {
            Assert.True((await Ocr().VerifyFieldAsync(new VerifyFieldRequest(f.FieldId, FieldVerificationDecision.AcceptedAsRead, null, null))).Succeeded);
        }

        var view = (await Reconciliation().GetAsync(upload.Value))!;
        var row = view.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.CustodyRow);
        Assert.Equal("1 | 01 SEP 26 | SMITH, TEST A. | JONES, TEST D. | HAND RECEIPT TO CASE AGENT", row.DocumentValue);
        var decided = await Reconciliation().DecideAsync(new ReconciliationDecisionRequest(upload.Value, row.FieldKey, ReconciliationDecision.RecordMissingHistoricalEvent, "Hand receipt between agents before submission; not in EMC (TEST)."));
        Assert.True(decided.Succeeded, decided.Error);
        Assert.Contains(decided.Warnings, w => w.Contains("REC-010", StringComparison.Ordinal) && w.Contains("Nothing is recorded from the scan by itself", StringComparison.Ordinal));
        return (voucher.Value, item.Value, upload.Value, decided.Value);
    }

    [Fact]
    public async Task ACustodyRowIsRecordedOnlyByTheCustodiansExplicitAct_WithThePapersDateAndTheScanAsProvenance()
    {
        var (voucherId, itemId, documentId, findingId) = await AcceptedVoucherWithACustodyRowFindingAsync();

        // The finding alone changed nothing.
        _harness.Db.ChangeTracker.Clear();
        var before = (await _harness.History.GetAsync(itemId))!;
        Assert.DoesNotContain(before.History, h => h.Kind == ItemEventKind.Custody);
        var view = (await Reconciliation().GetAsync(documentId))!;
        var row = view.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.CustodyRow);
        Assert.True(row.AwaitsCustodyRecording);
        Assert.Null(row.RecordedCustodyEventId);

        // The agent cannot record it; the custodian does, naming the parties and the paper's date.
        _harness.SignInAsAgent();
        var occurred = new DateTimeOffset(2026, 9, 1, 14, 30, 0, TimeSpan.FromHours(-4));
        var request = new RecordHistoricalCustodyEventRequest(itemId,
            new CustodyPartyInput(CustodyPartyKind.InternalUser, UserId: _harness.AgentUserId),
            new CustodyPartyInput(CustodyPartyKind.ExternalPerson, "JONES, TEST D.", TitleOrGrade: "SA", OrganizationOrAgency: "TEST FIELD OFFICE", IdentificationVerified: true),
            "HAND RECEIPT TO CASE AGENT", occurred, false, null, null, null, documentId, findingId);
        Assert.False((await _harness.Custody.RecordHistoricalCustodyEventAsync(request)).Succeeded);

        _harness.SignInAsCustodian();
        var recorded = await _harness.Custody.RecordHistoricalCustodyEventAsync(request);
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Contains(recorded.Warnings, w => w.Contains("no temporary release was created", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().SingleAsync(e => e.EvidenceItemId == itemId);
        Assert.Equal(occurred, custody.OccurredAtUtc);
        Assert.Equal(_harness.Clock.UtcNow, custody.RecordedAtUtc);
        Assert.NotEqual(custody.OccurredAtUtc, custody.RecordedAtUtc);
        Assert.Equal(documentId, custody.SourceDocumentId);
        Assert.Equal(findingId, custody.ReconciliationFindingId);
        Assert.Equal(_harness.AgentUserId, custody.ReleasedBy.UserId);
        Assert.Equal("JONES, TEST D.", custody.ReceivedBy.DisplayName);
        Assert.Contains($"reconciliation finding {findingId}", custody.Notes, StringComparison.Ordinal);
        Assert.Equal(recorded.Value, custody.Id);

        // No status change, no release, no correction, no MFR; the chain is intact and the
        // history shows the row in its historical place.
        var item = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, item.AccountabilityStatus);
        Assert.Empty(_harness.Db.TemporaryReleases);
        Assert.Empty(await _harness.Db.ItemEvents.OfType<CorrectionEvent>().ToListAsync());
        var history = (await _harness.History.GetAsync(itemId))!;
        Assert.True(history.ChainVerification.IsIntact);
        Assert.Equal("JONES, TEST D.", history.CurrentCustodyHolder);
        Assert.Equal(ItemEventKind.Custody, history.History.First(h => h.OccurredAtLocal == occurred).Kind);

        // The reconciliation view now says so, and the finding cannot be used twice.
        view = (await Reconciliation().GetAsync(documentId))!;
        row = view.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.CustodyRow);
        Assert.False(row.AwaitsCustodyRecording);
        Assert.Equal(custody.Id, row.RecordedCustodyEventId);
        var twice = await _harness.Custody.RecordHistoricalCustodyEventAsync(request);
        Assert.False(twice.Succeeded);
        Assert.Equal("REC-010", twice.RequirementId);
        Assert.Equal(voucherId, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId)).VoucherId);
    }

    [Fact]
    public async Task TheFindingMustBeThisVouchersCustodyRow_TheDateCannotBeInTheFuture_AndTheScanMustBeThisRooms()
    {
        var (_, itemId, documentId, findingId) = await AcceptedVoucherWithACustodyRowFindingAsync();
        _harness.SignInAsCustodian();
        var from = new CustodyPartyInput(CustodyPartyKind.InternalUser, UserId: _harness.AgentUserId);
        var to = new CustodyPartyInput(CustodyPartyKind.ExternalPerson, "JONES, TEST D.", IdentificationVerified: true);
        RecordHistoricalCustodyEventRequest Request(int? doc, int? finding, DateTimeOffset? at = null)
            => new(itemId, from, to, "HAND RECEIPT", at ?? _harness.Clock.UtcNow.AddDays(-2), false, null, null, null, doc, finding);

        Assert.Equal("REC-010", (await _harness.Custody.RecordHistoricalCustodyEventAsync(Request(documentId, 999_999))).RequirementId);
        Assert.Equal("REC-006", (await _harness.Custody.RecordHistoricalCustodyEventAsync(Request(999_999, null))).RequirementId);
        Assert.Equal("COC-003", (await _harness.Custody.RecordHistoricalCustodyEventAsync(Request(documentId, findingId, _harness.Clock.UtcNow.AddHours(1)))).RequirementId);

        // A backfill with no finding at all is still the custodian's act, with the scan as provenance.
        var plain = await _harness.Custody.RecordHistoricalCustodyEventAsync(Request(documentId, null));
        Assert.True(plain.Succeeded, plain.Error);
        _harness.Db.ChangeTracker.Clear();
        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().SingleAsync(e => e.Id == plain.Value);
        Assert.Equal(documentId, custody.SourceDocumentId);
        Assert.Null(custody.ReconciliationFindingId);
        var withFinding = await _harness.Custody.RecordHistoricalCustodyEventAsync(Request(documentId, findingId));
        Assert.True(withFinding.Succeeded, withFinding.Error); // the finding was not used by the plain backfill
    }

    private sealed class FixedMapper : IFormTemplateMapper
    {
        private readonly (string Key, string Value)[] _fields;
        public FixedMapper((string Key, string Value)[] fields) => _fields = fields;
        public string TemplateId => "test-fixed/1";
        public decimal IdentificationThreshold => 0.5m;
        public decimal Identify(IReadOnlyList<RecognizedPage> pages) => 1m;
        public IReadOnlyList<ExtractedFieldCandidate> Map(IReadOnlyList<RecognizedPage> pages)
            => _fields.Select((f, i) => new ExtractedFieldCandidate(f.Key, 1, f.Value, null, 95m, 0, i * 10, 10, 10)).ToList();
    }

    private sealed class Passthrough : IImagePreprocessor
    {
        public string Version => "passthrough/1";
        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default) => new(png, 10, 10, 0, 0, sourceDpi);
    }
}
