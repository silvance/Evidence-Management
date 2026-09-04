using Emc.Application.Cases;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Application.Reconciliation;
using Emc.Domain.Cases;
using Emc.Domain.Documents;
using Emc.Domain.Ocr;
using Emc.Domain.Reconciliation;
using Emc.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Reconciliation patches ONE field and nothing else; decisions are bound to the run and the
/// values compared; conflicting readings block; non-applicable fields are never "applied";
/// a voucher-attached document belongs to the voucher's case.
/// Requirements: ITEM-008, REC-007, REC-008, REC-009, DOC-013.
/// </summary>
public class ReconciliationPatchTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _docOptions;
    private readonly FileSystemSourceDocumentStore _store;

    public ReconciliationPatchTests()
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
    private IReconciliationService Service() => new ReconciliationService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, Ocr(), _harness.Reads, _harness.Vouchers);
    private ISourceDocumentService Documents() => new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_docOptions));

    private async Task<(int VoucherId, int ItemId)> DraftWithItemAsync(AddItemRequest? item = null)
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Patch test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A., SGT", _harness.Clock.UtcNow, false, null));
        var added = await _harness.Vouchers.AddItemAsync(item is null
            ? new AddItemRequest(voucher.Value, "ONE TEST BAG OF WHITE POWDER", "1", null, null, false, false, false, null)
            : item with { VoucherId = voucher.Value });
        Assert.True(added.Succeeded, added.Error);
        return (voucher.Value, added.Value);
    }

    private async Task<(int DocumentId, int RunId)> RunAsync(int voucherId, IEnumerable<(string Key, string Value, int Page)> fields, bool verify = true)
    {
        var upload = await Documents().UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.Pages(2), "UNCLASSIFIED"));
        Assert.True(upload.Succeeded, upload.Error);
        await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions);
        return await RerunAsync(upload.Value, fields, verify);
    }

    private async Task<(int DocumentId, int RunId)> RerunAsync(int documentId, IEnumerable<(string Key, string Value, int Page)> fields, bool verify = true)
    {
        Assert.True((await Ocr().RequestAsync(documentId)).Succeeded);
        var processor = new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine([("x", 95m)]), new Passthrough(), [new PagedMapper(fields.ToList())], _harness.Clock,
            Options.Create(new OcrOptions { WorkerId = "w" }), NullLogger<OcrJobProcessor>.Instance);
        Assert.True(await processor.ProcessNextAsync());
        var status = await Ocr().GetStatusAsync(documentId);
        if (verify)
        {
            foreach (var f in status!.LatestRun!.Fields)
            {
                Assert.True((await Ocr().VerifyFieldAsync(new VerifyFieldRequest(f.FieldId, FieldVerificationDecision.AcceptedAsRead, null, null))).Succeeded);
            }
        }

        return (documentId, status!.LatestRun!.RunId);
    }

    private async Task<EvidenceItem> ItemAsync(int itemId)
    {
        _harness.Db.ChangeTracker.Clear();
        return await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
    }

    [Fact]
    public async Task AQuantityChangeOnABiohazardItemChangesTheQuantityAndNothingElse()
    {
        // ITEM-008. The rendered description carries POSSIBLE BIOHAZARD; the raw one must not.
        var (voucherId, itemId) = await DraftWithItemAsync(new AddItemRequest(0, "ONE TEST SYRINGE, USED", "1", "TESTSERIAL000001", "000000000000001", true, false, true, "Sealed in a test biohazard bag"));
        var before = await ItemAsync(itemId);
        Assert.Equal("ONE TEST SYRINGE, USED POSSIBLE BIOHAZARD", before.DescriptionForForm);

        var (documentId, _) = await RunAsync(voucherId, [("Item[1].ItemNumber", "1", 1), ("Item[1].Quantity", "2", 1)]);
        var applied = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", ReconciliationDecision.AppliedToDraftForm, null));
        Assert.True(applied.Succeeded, applied.Error);

        var after = await ItemAsync(itemId);
        Assert.Equal("2", after.Quantity);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal("ONE TEST SYRINGE, USED", after.Description);
        Assert.Equal("ONE TEST SYRINGE, USED POSSIBLE BIOHAZARD", after.DescriptionForForm);
        Assert.Equal(before.IsPossibleBiohazard, after.IsPossibleBiohazard);
        Assert.Equal(before.IsFungible, after.IsFungible);
        Assert.Equal(before.IsSealed, after.IsSealed);
        Assert.Equal(before.SealDescription, after.SealDescription);
        Assert.Equal(before.SerialNumber, after.SerialNumber);
        Assert.Equal(before.UniqueDeviceIdentifier, after.UniqueDeviceIdentifier);
        Assert.Equal(before.IsCurrency, after.IsCurrency);
    }

    [Fact]
    public async Task ASerialChangeOnAFungibleItemChangesOnlyTheSerial_AndAUdiChangeOnASealedItemOnlyTheUdi()
    {
        var (voucherId, itemId) = await DraftWithItemAsync(new AddItemRequest(0, "TEST TABLETS, WHITE", "APPROX 100", "OLDSERIAL", "000000000000009", false, true, true, "Sealed in a marked test envelope"));
        var before = await ItemAsync(itemId);

        var (documentId, _) = await RunAsync(voucherId, [("Item[1].ItemNumber", "1", 1), ("Item[1].SerialNumber", "TESTSERIAL000002", 1), ("Item[1].UniqueDeviceIdentifier", "000000000000002", 1)]);
        Assert.True((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].SerialNumber", ReconciliationDecision.AppliedToDraftForm, null))).Succeeded);
        var mid = await ItemAsync(itemId);
        Assert.Equal("TESTSERIAL000002", mid.SerialNumber);
        Assert.Equal(before.UniqueDeviceIdentifier, mid.UniqueDeviceIdentifier);
        Assert.True(mid.IsFungible);
        Assert.True(mid.IsSealed);
        Assert.Equal(before.SealDescription, mid.SealDescription);
        Assert.Equal(before.Description, mid.Description);
        Assert.Equal(before.Quantity, mid.Quantity);

        Assert.True((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].UniqueDeviceIdentifier", ReconciliationDecision.AppliedToDraftForm, null))).Succeeded);
        var after = await ItemAsync(itemId);
        Assert.Equal("000000000000002", after.UniqueDeviceIdentifier);
        Assert.Equal("TESTSERIAL000002", after.SerialNumber);
        Assert.True(after.IsSealed);
        Assert.Equal(before.SealDescription, after.SealDescription);
        Assert.True(after.IsFungible);
    }

    [Fact]
    public async Task ADescriptionChangeStoresTheRawDescription_NeverTheRenderedAnnotation()
    {
        var (voucherId, itemId) = await DraftWithItemAsync(new AddItemRequest(0, "ONE TEST SYRINGE", "1", null, null, true, false, false, null));
        // The paper shows the rendered text; the verifier accepts it as read.
        var (documentId, _) = await RunAsync(voucherId, [("Item[1].ItemNumber", "1", 1), ("Item[1].Description", "ONE TEST SYRINGE, CAPPED POSSIBLE BIOHAZARD", 1)]);
        Assert.True((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Description", ReconciliationDecision.AppliedToDraftForm, null))).Succeeded);

        var after = await ItemAsync(itemId);
        Assert.Equal("ONE TEST SYRINGE, CAPPED", after.Description);
        Assert.Equal("ONE TEST SYRINGE, CAPPED POSSIBLE BIOHAZARD", after.DescriptionForForm);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(after.DescriptionForForm, "POSSIBLE BIOHAZARD"));
        Assert.True(after.IsPossibleBiohazard);

        // And the difference is gone: what was applied is what the scan says.
        var view = await Service().GetAsync(documentId);
        Assert.DoesNotContain(view!.Differences, d => d.FieldKey == "Item[1].Description" && !d.IsResolved);
        Assert.Equal("ONE TEST SYRINGE", EvidenceItem.RawDescriptionFromForm("ONE TEST SYRINGE POSSIBLE BIOHAZARD POSSIBLE BIOHAZARD"));
    }

    [Fact]
    public async Task ACaseControlNumberIsNeverReportedAsApplied()
    {
        // REC-007. It used to reach the header path, change nothing, and be recorded as applied.
        var (voucherId, _) = await DraftWithItemAsync();
        var (documentId, _) = await RunAsync(voucherId, [(OcrFieldCatalog.CaseControlNumber, "TEST-CI-2026-9999", 1)]);
        var view = await Service().GetAsync(documentId);
        var difference = Assert.Single(view!.Differences);
        Assert.Equal(DifferenceApplicability.NotAppliedCaseControlNumber, difference.Applicability);

        var refused = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, OcrFieldCatalog.CaseControlNumber, ReconciliationDecision.AppliedToDraftForm, null));
        Assert.False(refused.Succeeded);
        Assert.Equal("REC-007", refused.RequirementId);
        Assert.Empty(await _harness.Db.ReconciliationFindings.AsNoTracking().ToListAsync());

        var flagged = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, OcrFieldCatalog.CaseControlNumber, ReconciliationDecision.FlagForCustodianReview, "Case number on the form differs."));
        Assert.True(flagged.Succeeded, flagged.Error);
    }

    [Fact]
    public async Task AnExtraItemAndACustodyRowAndADispositionEntryAreNeverApplied()
    {
        var (voucherId, _) = await DraftWithItemAsync();
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucherId, "SECOND TEST ITEM", "1", null, null, false, false, false, null));
        var (documentId, _) = await RunAsync(voucherId,
        [
            ("Item[1].ItemNumber", "1", 1), ("Item[1].Description", "ONE TEST BAG OF WHITE POWDER", 1),
            ("Custody[1].Date", "03 SEP 26", 1), ("Custody[1].ReleasedByName", "SMITH, TEST A.", 1), ("Custody[1].ReceivedByName", "JONES, TEST B.", 1),
            (OcrFieldCatalog.DispositionAction, "DESTROYED (TEST)", 2)
        ]);
        var view = await Service().GetAsync(documentId);
        Assert.Equal(DifferenceApplicability.ReviewOrWithdraw, view!.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.ExtraItem).Applicability);
        Assert.Equal(DifferenceApplicability.CustodyWorkflow, view.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.CustodyRow).Applicability);
        Assert.Equal(DifferenceApplicability.DispositionWorkflow, view.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.Disposition).Applicability);
        foreach (var d in view.Differences)
        {
            var refused = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, d.FieldKey, ReconciliationDecision.AppliedToDraftForm, null));
            Assert.Equal("REC-007", refused.RequirementId);
        }

        Assert.Equal(2, (await _harness.Reads.GetVoucherAsync(voucherId))!.Items.Count);
    }

    [Fact]
    public async Task AFindingBindsToItsRunAndItsValues_NotToTheFieldKey()
    {
        // REC-008.
        var (voucherId, itemId) = await DraftWithItemAsync();
        var (documentId, run1) = await RunAsync(voucherId, [("Item[1].ItemNumber", "1", 1), ("Item[1].Quantity", "2", 1)]);
        var decided = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", ReconciliationDecision.CompanionRecordAlreadyCorrect, null));
        Assert.True(decided.Succeeded, decided.Error);
        Assert.True((await Service().GetAsync(documentId))!.Differences.Single().IsResolved);

        // A new run reads a different value: new work.
        var (_, run2) = await RerunAsync(documentId, [("Item[1].ItemNumber", "1", 1), ("Item[1].Quantity", "3", 1)]);
        Assert.NotEqual(run1, run2);
        var view = await Service().GetAsync(documentId);
        var d = Assert.Single(view!.Differences);
        Assert.Equal("3", d.DocumentValue);
        Assert.False(d.IsResolved);

        // The same value as run 1, on run 2: still new work - nothing is carried forward.
        var (_, run3) = await RerunAsync(documentId, [("Item[1].ItemNumber", "1", 1), ("Item[1].Quantity", "2", 1)]);
        d = Assert.Single((await Service().GetAsync(documentId))!.Differences);
        Assert.Equal("2", d.DocumentValue);
        Assert.False(d.IsResolved);
        Assert.True((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", ReconciliationDecision.CompanionRecordAlreadyCorrect, null))).Succeeded);
        Assert.True((await Service().GetAsync(documentId))!.Differences.Single().IsResolved);

        // The companion value changes on the same run: the finding compared "1" vs "2"; this is "5" vs "2".
        Assert.True((await _harness.Vouchers.UpdateDraftItemFieldAsync(new UpdateDraftItemFieldRequest(itemId, DraftItemField.Quantity, "5"))).Succeeded);
        d = Assert.Single((await Service().GetAsync(documentId))!.Differences);
        Assert.Equal("5", d.CompanionValue);
        Assert.False(d.IsResolved);

        // The same key on another item is another difference.
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucherId, "SECOND TEST ITEM", "1", null, null, false, false, false, null));
        await RerunAsync(documentId, [("Item[1].ItemNumber", "1", 1), ("Item[1].Quantity", "5", 1), ("Item[2].ItemNumber", "2", 1), ("Item[2].Quantity", "2", 1)]);
        view = await Service().GetAsync(documentId);
        d = Assert.Single(view!.Differences);
        Assert.Equal("Item[2].Quantity", d.FieldKey);
        Assert.False(d.IsResolved);

        // A legitimate correction that makes the values agree: the difference disappears; three decisions stay on record.
        Assert.True((await _harness.Vouchers.UpdateDraftItemFieldAsync(new UpdateDraftItemFieldRequest(view.Differences.Single().EvidenceItemId!.Value, DraftItemField.Quantity, "2"))).Succeeded);
        view = await Service().GetAsync(documentId);
        Assert.Empty(view!.Differences);
        Assert.Equal(2, view.Findings.Count);
        Assert.All(view.Findings, f => Assert.Contains(f.OcrRunId, new[] { run1, run3 }));
    }

    [Fact]
    public async Task ConflictingVerifiedReadingsAcrossPagesBlockEveryDecisionThatReliesOnOne()
    {
        // REC-009. Front says 037-26, back says 038-26; both verified as read.
        var (voucherId, _) = await DraftWithItemAsync();
        var (documentId, _) = await RunAsync(voucherId, [(OcrFieldCatalog.DocumentNumber, "037-26", 1), (OcrFieldCatalog.DocumentNumber, "038-26", 2), ("Item[1].ItemNumber", "1", 1), ("Item[1].Quantity", "7", 1), ("Item[1].Quantity", "9", 2)]);
        var view = await Service().GetAsync(documentId);
        var number = view!.Differences.Single(d => d.FieldKey == OcrFieldCatalog.DocumentNumber);
        var quantity = view.Differences.Single(d => d.FieldKey == "Item[1].Quantity");
        Assert.True(number.IsConflicted);
        Assert.True(quantity.IsConflicted);
        Assert.Equal(2, quantity.ConflictingValues!.Count);
        Assert.Equal(2, view.ConflictedDifferences);

        foreach (var decision in new[] { ReconciliationDecision.AppliedToDraftForm, ReconciliationDecision.InitiatePostAcceptanceCorrection, ReconciliationDecision.RecordMissingHistoricalEvent })
        {
            var refused = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", decision, "x"));
            Assert.False(refused.Succeeded);
            Assert.Equal("REC-009", refused.RequirementId);
        }

        Assert.Equal("1", (await _harness.Reads.GetVoucherAsync(voucherId))!.Items[0].Quantity);

        // A person resolves it at verification: the back page's reading is not the document's.
        var status = await Ocr().GetStatusAsync(documentId);
        var backQuantity = status!.LatestRun!.Fields.Single(f => f.FieldKey == "Item[1].Quantity" && f.PageNumber == 2);
        Assert.True((await Ocr().VerifyFieldAsync(new VerifyFieldRequest(backQuantity.FieldId, FieldVerificationDecision.NotApplicable, null, "Back page reading is a different block"))).Succeeded);
        view = await Service().GetAsync(documentId);
        quantity = view!.Differences.Single(d => d.FieldKey == "Item[1].Quantity");
        Assert.False(quantity.IsConflicted);
        Assert.Equal("7", quantity.DocumentValue);
        Assert.Equal(DifferenceApplicability.AppliesToDraft, quantity.Applicability);
        Assert.True((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", ReconciliationDecision.AppliedToDraftForm, null))).Succeeded);
        Assert.Equal("7", (await _harness.Reads.GetVoucherAsync(voucherId))!.Items[0].Quantity);

        // Both readings and both verification decisions remain on record.
        status = await Ocr().GetStatusAsync(documentId);
        Assert.Equal(2, status!.LatestRun!.Fields.Count(f => f.FieldKey == "Item[1].Quantity"));
        Assert.Equal(2, status.LatestRun.Fields.Single(f => f.FieldId == backQuantity.FieldId).History.Count);
    }

    [Fact]
    public async Task AVoucherAttachedDocumentBelongsToTheVouchersCase()
    {
        // DOC-013. Same room, different case: refused. Blank case: derived from the voucher.
        var (voucherId, _) = await DraftWithItemAsync();
        var voucherCaseId = (await _harness.Reads.GetVoucherAsync(voucherId))!.CaseId;
        var otherCase = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Other case", null, _harness.EvidenceRoomId));

        var mismatched = await Documents().UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, otherCase.Value, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalCopy, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        Assert.False(mismatched.Succeeded);
        Assert.Equal("DOC-013", mismatched.RequirementId);

        var derived = await Documents().UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalCopy, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        Assert.True(derived.Succeeded, derived.Error);
        var stored = await _harness.Db.SourceDocuments.AsNoTracking().SingleAsync(d => d.Id == derived.Value);
        Assert.Equal(voucherCaseId, stored.CaseId);
        Assert.Equal(voucherId, stored.VoucherId);

        // Cross-room: the case is another room's; the voucher decides the room, and the case is not the voucher's.
        var crossRoom = await Documents().UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, 999_999, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalCopy, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        Assert.False(crossRoom.Succeeded);
        Assert.Equal("DOC-013", crossRoom.RequirementId);
        Assert.Equal(1, await _harness.Db.SourceDocuments.AsNoTracking().CountAsync(d => d.VoucherId == voucherId));
    }

    private sealed class Passthrough : IImagePreprocessor
    {
        public string Version => "passthrough/1";
        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default) => new(png, 10, 10, 0, 0, sourceDpi);
    }

    private sealed class PagedMapper : IFormTemplateMapper
    {
        private readonly List<(string Key, string Value, int Page)> _fields;
        public PagedMapper(List<(string Key, string Value, int Page)> fields) => _fields = fields;
        public string TemplateId => "test-paged/1";
        public decimal IdentificationThreshold => 0.5m;
        public decimal Identify(IReadOnlyList<RecognizedPage> pages) => 1m;
        public IReadOnlyList<ExtractedFieldCandidate> Map(IReadOnlyList<RecognizedPage> pages)
            => _fields.Select((f, i) => new ExtractedFieldCandidate(f.Key, f.Page, f.Value, null, 95m, 0, i * 10, 10, 10)).ToList();
    }
}
