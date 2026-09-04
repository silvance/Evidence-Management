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
/// Reconciliation: the verified scan against the companion record. Draft changes go through the
/// voucher service and only before acceptance; after acceptance everything is a finding and a
/// true error goes through para 1-7c(3) with the scan as provenance; a document number is never
/// applied. Requirements: REC-001 .. REC-004, OCR-001, VCH-025.
/// </summary>
public class ReconciliationTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emc-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceDocumentOptions _docOptions;
    private readonly FileSystemSourceDocumentStore _store;

    public ReconciliationTests()
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

    private async Task<int> VoucherAsync(bool accept, string number = "005-26")
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Reconcile test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A., SGT", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "ONE TEST MOBILE TELEPHONE, BLACK", "1", null, "000000000000001", false, false, false, null));
        if (accept)
        {
            await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucher.Value);
            _harness.SignInAsCustodian();
            var numbered = await _harness.Intake.RecordOfficialDocumentNumberAsync(new RecordDocumentNumberRequest(voucher.Value, number, true, _harness.Clock.UtcNow));
            Assert.True(numbered.Succeeded, numbered.Error);
            _harness.SignInAsAgent();
        }

        return voucher.Value;
    }

    /// <summary>Uploads a page, runs OCR with a mapper that emits the given DA 4137 fields at 95, and verifies every field as read.</summary>
    private async Task<int> DocumentWithVerifiedFieldsAsync(int voucherId, params (string Key, string Value)[] fields)
    {
        var documents = new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_docOptions));
        var upload = await documents.UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        Assert.True(upload.Succeeded, upload.Error);
        await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions);
        Assert.True((await Ocr().RequestAsync(upload.Value)).Succeeded);

        var processor = new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine([("x", 95m)]), new Passthrough(), [new FixedMapper(fields)], _harness.Clock,
            Options.Create(new OcrOptions { WorkerId = "w" }), NullLogger<OcrJobProcessor>.Instance);
        Assert.True(await processor.ProcessNextAsync());

        var status = await Ocr().GetStatusAsync(upload.Value);
        foreach (var f in status!.LatestRun!.Fields)
        {
            var verified = await Ocr().VerifyFieldAsync(new VerifyFieldRequest(f.FieldId, FieldVerificationDecision.AcceptedAsRead, null, null));
            Assert.True(verified.Succeeded, verified.Error);
        }

        return upload.Value;
    }

    [Fact]
    public async Task ADraftIsChangedOnlyByAnExplicitApplyDecision_ThroughTheVoucherService()
    {
        var voucherId = await VoucherAsync(accept: false);
        var documentId = await DocumentWithVerifiedFieldsAsync(voucherId,
            (OcrFieldCatalog.ReceivingActivity, "TEST EVIDENCE ROOM"),
            ("Item[1].ItemNumber", "1"), ("Item[1].Description", "ONE TEST MOBILE TELEPHONE, BLACK, CRACKED SCREEN"), ("Item[1].UniqueDeviceIdentifier", "000000000000001"),
            ("Item[2].ItemNumber", "2"), ("Item[2].Description", "ONE TEST CHARGER, WHITE"), ("Item[2].Quantity", "1"));

        var view = await Service().GetAsync(documentId);
        Assert.NotNull(view);
        Assert.True(view.IsPreAcceptance);
        Assert.True(view.VerificationComplete);
        var keys = view.Differences.Select(d => d.FieldKey).ToList();
        Assert.Contains("Item[1].Description", keys);        // differs
        Assert.Contains("Item[2].Description", keys);        // missing on the companion record
        Assert.DoesNotContain(OcrFieldCatalog.ReceivingActivity, keys); // same
        Assert.DoesNotContain("Item[1].UniqueDeviceIdentifier", keys);  // same
        Assert.Equal(ReconciliationDifferenceKind.MissingItem, view.Differences.Single(d => d.FieldKey == "Item[2].Description").Kind);

        // Viewing changes nothing (REC-001).
        var before = await _harness.Reads.GetVoucherAsync(voucherId);
        Assert.Equal("ONE TEST MOBILE TELEPHONE, BLACK", before!.Items[0].DescriptionForForm);
        Assert.Single(before.Items);

        var applied = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Description", ReconciliationDecision.AppliedToDraftForm, null));
        Assert.True(applied.Succeeded, applied.Error);
        var added = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[2].Description", ReconciliationDecision.AppliedToDraftForm, null));
        Assert.True(added.Succeeded, added.Error);

        var after = await _harness.Reads.GetVoucherAsync(voucherId);
        Assert.Equal("ONE TEST MOBILE TELEPHONE, BLACK, CRACKED SCREEN", after!.Items[0].DescriptionForForm);
        Assert.Equal(2, after.Items.Count);
        Assert.Equal("ONE TEST CHARGER, WHITE", after.Items[1].DescriptionForForm);

        view = await Service().GetAsync(documentId);
        Assert.Empty(view!.Differences.Where(d => !d.IsResolved));
        Assert.Equal(2, view.Findings.Count);
        Assert.All(view.Findings, f => Assert.Equal(ReconciliationDecision.AppliedToDraftForm, f.Decision));
    }

    [Fact]
    public async Task ADocumentNumberIsNeverAppliedFromAScan()
    {
        var voucherId = await VoucherAsync(accept: false);
        var documentId = await DocumentWithVerifiedFieldsAsync(voucherId, (OcrFieldCatalog.DocumentNumber, "009-26"));

        var view = await Service().GetAsync(documentId);
        var difference = Assert.Single(view!.Differences);
        Assert.Equal(ReconciliationDifferenceKind.DocumentNumber, difference.Kind);
        Assert.Null(difference.CompanionValue);
        Assert.Equal("009-26", difference.DocumentValue);

        var refused = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, OcrFieldCatalog.DocumentNumber, ReconciliationDecision.AppliedToDraftForm, null));
        Assert.False(refused.Succeeded);
        Assert.Equal("REC-005", refused.RequirementId);
        Assert.Empty((await _harness.Reads.GetVoucherAsync(voucherId))!.DocumentNumbers);

        // The domain refuses it too, whatever the service does.
        Assert.Throws<DomainRuleViolationException>(() => new ReconciliationFinding(1, 1, 1, null, ReconciliationDifferenceKind.DocumentNumber, OcrFieldCatalog.DocumentNumber, null, "009-26", ReconciliationDecision.AppliedToDraftForm, null, 1, _harness.Clock.UtcNow));

        var flagged = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, OcrFieldCatalog.DocumentNumber, ReconciliationDecision.FlagForCustodianReview, "Form shows 009-26; not yet recorded."));
        Assert.True(flagged.Succeeded, flagged.Error);
    }

    [Fact]
    public async Task NothingIsAppliedWhileMandatoryVerificationIsOutstanding()
    {
        var voucherId = await VoucherAsync(accept: false);
        var documents = new SourceDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock, _store, Options.Create(_docOptions));
        var upload = await documents.UploadAsync(new UploadSourceDocumentRequest(_harness.EvidenceRoomId, null, voucherId, SourceDocumentType.DaForm4137, ScanProvenance.PhysicalOriginal, "scan.pdf", SyntheticPdf.SinglePage(), "UNCLASSIFIED"));
        await TestRendering.RenderAllAsync(_harness.Db, _store, _harness.Clock, _docOptions);
        await Ocr().RequestAsync(upload.Value);
        var processor = new OcrJobProcessor(_harness.Db, _store, new OcrProcessorTests.FakeEngine([("x", 95m)]), new Passthrough(),
            [new FixedMapper([("Item[1].ItemNumber", "1"), ("Item[1].Description", "ONE TEST MOBILE TELEPHONE, BLACK, CRACKED"), ("Item[1].SerialNumber", "TESTSERIAL000009")])],
            _harness.Clock, Options.Create(new OcrOptions { WorkerId = "w" }), NullLogger<OcrJobProcessor>.Instance);
        await processor.ProcessNextAsync();

        // The serial number (high-consequence) is unverified.
        var view = await Service().GetAsync(upload.Value);
        Assert.False(view!.VerificationComplete);
        var refused = await Service().DecideAsync(new ReconciliationDecisionRequest(upload.Value, "Item[1].Description", ReconciliationDecision.AppliedToDraftForm, null));
        Assert.False(refused.Succeeded);
        Assert.Equal("REC-002", refused.RequirementId);
        Assert.Equal("ONE TEST MOBILE TELEPHONE, BLACK", (await _harness.Reads.GetVoucherAsync(voucherId))!.Items[0].DescriptionForForm);
    }

    [Fact]
    public async Task AfterAcceptance_NothingIsApplied_AndATrueErrorGoesThrough1_7c3WithProvenance()
    {
        var voucherId = await VoucherAsync(accept: true, number: "006-26");
        var documentId = await DocumentWithVerifiedFieldsAsync(voucherId,
            (OcrFieldCatalog.DocumentNumber, "006-26"),
            ("Item[1].ItemNumber", "1"), ("Item[1].Description", "ONE TEST MOBILE TELEPHONE, BLUE"),
            ("Custody[1].ItemNumber", "1"), ("Custody[1].Date", "03 SEP 26"), ("Custody[1].ReleasedByName", "SMITH, TEST A."), ("Custody[1].ReceivedByName", "BAKER, TEST C."), ("Custody[1].Purpose", "RELEASED TO CUSTODIAN"));

        var view = await Service().GetAsync(documentId);
        Assert.False(view!.IsPreAcceptance);
        Assert.DoesNotContain(view.Differences, d => d.FieldKey == OcrFieldCatalog.DocumentNumber); // same number: no difference
        var description = view.Differences.Single(d => d.FieldKey == "Item[1].Description");
        var custody = view.Differences.Single(d => d.Kind == ReconciliationDifferenceKind.CustodyRow);

        // The agent cannot apply, and cannot initiate a 1-7c(3) correction.
        var applied = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, description.FieldKey, ReconciliationDecision.AppliedToDraftForm, null));
        Assert.False(applied.Succeeded);
        Assert.Equal("REC-002", applied.RequirementId);
        var agentInitiates = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, description.FieldKey, ReconciliationDecision.InitiatePostAcceptanceCorrection, "Colour wrong"));
        Assert.False(agentInitiates.Succeeded);
        Assert.Equal("IAM-005", agentInitiates.RequirementId);

        // A custody row on the paper the record lacks: a finding for the custody workflow, with a narrative.
        var missingNoNarrative = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, custody.FieldKey, ReconciliationDecision.RecordMissingHistoricalEvent, null));
        Assert.False(missingNoNarrative.Succeeded);
        Assert.Equal("REC-004", missingNoNarrative.RequirementId);
        var missing = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, custody.FieldKey, ReconciliationDecision.RecordMissingHistoricalEvent, "Custody row 1 (release to custodian) is on the form and not in EMC."));
        Assert.True(missing.Succeeded, missing.Error);

        // The custodian initiates the 1-7c(3) path: a finding, then the correction on the item's history with the scan as provenance.
        _harness.SignInAsCustodian();
        var initiated = await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, description.FieldKey, ReconciliationDecision.InitiatePostAcceptanceCorrection, "The item is blue; the accepted description says black."));
        Assert.True(initiated.Succeeded, initiated.Error);
        Assert.Contains(initiated.Warnings, w => w.Contains("1-7c(3)", StringComparison.Ordinal));
        Assert.Equal("ONE TEST MOBILE TELEPHONE, BLACK", (await _harness.Reads.GetVoucherAsync(voucherId))!.Items[0].DescriptionForForm); // unchanged by the finding

        var itemId = description.EvidenceItemId!.Value;
        var history = await _harness.History.GetAsync(itemId);
        var acceptance = history!.History.First(e => e.Kind == ItemEventKind.Status || e.Kind == ItemEventKind.DocumentNumber);
        var correction = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            acceptance.EventId, "Notes", "Description on the form: ONE TEST MOBILE TELEPHONE, BLUE", "Reconciled from the verified scan; supervisor informed; MFR filed.",
            CorrectionCategory.PostAcceptanceAccountabilityRecord, "MFR TEST-2026-0001", _harness.CommanderUserId, _harness.Clock.UtcNow, SourceDocumentId: documentId));
        Assert.True(correction.Succeeded, correction.Error);

        var evt = await _harness.Db.ItemEvents.AsNoTracking().OfType<CorrectionEvent>().OrderByDescending(e => e.Id).FirstAsync(e => e.EvidenceItemId == itemId);
        Assert.Equal(documentId, evt.SourceDocumentId);
        Assert.Contains("SourceDocumentId", EventHashChain.Canonicalize(evt), StringComparison.Ordinal);

        // A document from another room cannot be named as provenance.
        var foreign = await _harness.History.RecordCorrectionAsync(new RecordCorrectionRequest(
            acceptance.EventId, "Notes", "x", "y", CorrectionCategory.PostAcceptanceAccountabilityRecord, "MFR", _harness.CommanderUserId, _harness.Clock.UtcNow, SourceDocumentId: 999_999));
        Assert.False(foreign.Succeeded);
        Assert.Equal("REC-004", foreign.RequirementId);
    }

    [Fact]
    public async Task FindingsAreAppendOnly_AndAnOutsiderSeesNothing()
    {
        var voucherId = await VoucherAsync(accept: false);
        var documentId = await DocumentWithVerifiedFieldsAsync(voucherId, ("Item[1].ItemNumber", "1"), ("Item[1].Quantity", "2"));
        Assert.True((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", ReconciliationDecision.CompanionRecordAlreadyCorrect, null))).Succeeded);

        _harness.Db.ChangeTracker.Clear();
        var finding = await _harness.Db.ReconciliationFindings.SingleAsync();
        _harness.Db.Entry(finding).Property(nameof(ReconciliationFinding.Narrative)).CurrentValue = "tampered";
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.ChangeTracker.Clear();
        _harness.Db.ReconciliationFindings.Remove(await _harness.Db.ReconciliationFindings.SingleAsync());
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.ChangeTracker.Clear();

        _harness.SignInAsAdministrator();
        Assert.Null(await Service().GetAsync(documentId));
        Assert.False((await Service().DecideAsync(new ReconciliationDecisionRequest(documentId, "Item[1].Quantity", ReconciliationDecision.ExtractionIncorrect, "x"))).Succeeded);
    }

    private sealed class Passthrough : IImagePreprocessor
    {
        public string Version => "passthrough/1";
        public PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default) => new(png, 10, 10, 0, 0, sourceDpi);
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
}
