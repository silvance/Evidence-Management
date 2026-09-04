using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Ocr;
using Emc.Application.Reads;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Ocr;
using Emc.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Reconciliation;

/// <summary>
/// What may be done with a difference. Decided by kind and field key, on the server, and
/// mirrored in the UI (REC-007). Only <see cref="AppliesToDraft"/> may be "applied"; everything
/// else is a finding routed to the workflow that owns the fact.
/// </summary>
public enum DifferenceApplicability
{
    /// <summary>Header receiving activity / location / received-from, or a supported item field, on a pre-acceptance voucher.</summary>
    AppliesToDraft = 1,

    /// <summary>A document number is never applied from a scan (REC-005).</summary>
    NeverAppliedDocumentNumber = 2,

    /// <summary>A case control number is never applied: a voucher's case is changed on the case, not from a scan.</summary>
    NotAppliedCaseControlNumber = 3,

    /// <summary>A chain-of-custody row: the custody workflow records it.</summary>
    CustodyWorkflow = 4,

    /// <summary>A final disposal entry: the disposition workflow records it.</summary>
    DispositionWorkflow = 5,

    /// <summary>A line on the record the scan lacks: reviewed, or withdrawn on a returned form (VCH-026); never removed from a scan.</summary>
    ReviewOrWithdraw = 6,

    /// <summary>The voucher is accepted: nothing is applied; findings only.</summary>
    AcceptedRecordFindingOnly = 7,

    /// <summary>The same field was verified with different values on different pages (REC-009). Nothing may rely on one of them until a person resolves the readings at verification.</summary>
    Conflicted = 8
}

/// <summary>One difference between the verified scan and the companion record, as computed now. Not stored: the draft patch is recomputed on every view (REC-001).</summary>
public sealed record ReconciliationDifference(
    string FieldKey,
    ReconciliationDifferenceKind Kind,
    int? EvidenceItemId,
    int? ItemNumber,
    string? CompanionValue,
    string? DocumentValue,
    bool DocumentValueVerified,
    DifferenceApplicability Applicability,
    string Explanation,
    ReconciliationFindingRow? LatestFinding,
    IReadOnlyList<string>? ConflictingValues = null,

    /// <summary>For a custody row decided "record missing historical event": the custody event a person then recorded from that finding, if any (REC-010).</summary>
    int? RecordedCustodyEventId = null,

    /// <summary>For a custody row: the one form line its item-number column names, when it names exactly one and the companion has it. The hand-off target for REC-010.</summary>
    int? CustodyItemId = null)
{
    /// <summary>Resolved only by a finding on THIS run, THIS kind, THIS item, and THESE two values (REC-008).</summary>
    public bool IsResolved => LatestFinding is not null;

    /// <summary>A custody row whose finding says "record it" and which no person has recorded yet - the explicit next step.</summary>
    public bool AwaitsCustodyRecording
        => Kind == ReconciliationDifferenceKind.CustodyRow && LatestFinding?.Decision == ReconciliationDecision.RecordMissingHistoricalEvent && RecordedCustodyEventId is null;

    public bool IsConflicted => Applicability == DifferenceApplicability.Conflicted;
}

public sealed record ReconciliationFindingRow(
    int Id, int OcrRunId, string FieldKey, ReconciliationDifferenceKind Kind, int? EvidenceItemId, string? CompanionValue, string? DocumentValue,
    ReconciliationDecision Decision, string? Narrative, string DecidedByName, DateTimeOffset DecidedAtUtc);

public sealed record ReconciliationView(
    int SourceDocumentId,
    int EvidenceRoomId,
    int VoucherId,
    string VoucherIdentifier,
    VoucherDerivedStatus VoucherStatus,
    bool IsPreAcceptance,
    int? RunId,
    bool VerificationComplete,
    int MandatoryVerificationsOutstanding,
    IReadOnlyList<ReconciliationDifference> Differences,
    IReadOnlyList<ReconciliationFindingRow> Findings,
    bool CanDecide,
    bool CanInitiatePostAcceptanceCorrection)
{
    public int OpenDifferences => Differences.Count(d => !d.IsResolved);
    public int ConflictedDifferences => Differences.Count(d => d.IsConflicted);
}

public sealed record ReconciliationDecisionRequest(int SourceDocumentId, string FieldKey, ReconciliationDecision Decision, string? Narrative);

/// <summary>
/// REC-001 to REC-009. Reconciliation is the ONLY place a verified scan meets the companion
/// record, and it is explicit: a person sees each difference and decides. Before acceptance the
/// decision "applied to draft form" changes ONE field of the draft through the ordinary voucher
/// service (so a returned form's resubmission is a new revision, VCH-025). After acceptance
/// nothing is applied: a finding is recorded, and a true error in the accepted record is taken to
/// the para 1-7c(3) correction workflow. A document number is never applied from a scan. A
/// difference is resolved only by a decision on the same run and the same two values; a
/// field whose verified readings conflict across pages cannot be acted on at all.
/// </summary>
public interface IReconciliationService
{
    /// <summary>Null when the document is absent, unauthorized, or not attached to a voucher.</summary>
    Task<ReconciliationView?> GetAsync(int sourceDocumentId, CancellationToken ct = default);

    Task<OperationResult<int>> DecideAsync(ReconciliationDecisionRequest request, CancellationToken ct = default);
}

public sealed class ReconciliationService : IReconciliationService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly IOcrJobService _ocr;
    private readonly IEvidenceReadService _reads;
    private readonly IVoucherService _vouchers;

    public ReconciliationService(
        IEmcDbContext db, IEvidenceAuthorizationService authorization, ICurrentUser currentUser, IAuditRecorder audit, IClock clock,
        IOcrJobService ocr, IEvidenceReadService reads, IVoucherService vouchers)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _clock = clock;
        _ocr = ocr;
        _reads = reads;
        _vouchers = vouchers;
    }

    public async Task<ReconciliationView?> GetAsync(int sourceDocumentId, CancellationToken ct = default)
    {
        var doc = await _db.SourceDocuments.AsNoTracking().Where(d => d.Id == sourceDocumentId).Select(d => new { d.EvidenceRoomId, d.VoucherId }).FirstOrDefaultAsync(ct);
        if (doc is null || doc.VoucherId is null)
        {
            return null;
        }

        if (!(await _authorization.AuthorizeAsync(EmcPermissions.ViewSourceDocument, doc.EvidenceRoomId, ct)).IsAllowed)
        {
            return null;
        }

        var voucher = await _reads.GetVoucherAsync(doc.VoucherId.Value, ct);
        var status = await _ocr.GetStatusAsync(sourceDocumentId, ct);
        if (voucher is null || status is null)
        {
            return null;
        }

        var run = status.LatestRun is { Outcome: OcrRunOutcome.Succeeded } r ? r : null;
        var findings = await FindingsAsync(sourceDocumentId, ct);

        // REC-010. Custody rows a person has since recorded from their finding.
        var findingIds = findings.Where(f => f.Kind == ReconciliationDifferenceKind.CustodyRow).Select(f => f.Id).ToList();
        var recordedFromFindings = findingIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.ItemEvents.OfType<CustodyEvent>().AsNoTracking()
                .Where(e => e.ReconciliationFindingId != null && findingIds.Contains(e.ReconciliationFindingId.Value))
                .ToDictionaryAsync(e => e.ReconciliationFindingId!.Value, e => e.Id, ct);

        var differences = run is null ? [] : Compute(voucher, run, findings, recordedFromFindings);
        var canDecide = (await _authorization.AuthorizeAsync(EmcPermissions.ReconcileSourceDocument, doc.EvidenceRoomId, ct)).IsAllowed;
        var canCorrect = (await _authorization.AuthorizeAsync(EmcPermissions.RecordCorrection, doc.EvidenceRoomId, ct)).IsAllowed;

        return new ReconciliationView(
            sourceDocumentId, doc.EvidenceRoomId, voucher.Id, voucher.DisplayIdentifier, voucher.DerivedStatus, voucher.AllowsItemEditing,
            run?.RunId, run?.VerificationComplete ?? false, run?.MandatoryOutstanding ?? 0, differences, findings, canDecide, canCorrect);
    }

    public async Task<OperationResult<int>> DecideAsync(ReconciliationDecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = await GetAsync(request.SourceDocumentId, ct);
        if (view is null)
        {
            return OperationResult<int>.Failure("The document was not found.", "REC-001");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ReconcileSourceDocument, view.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            return OperationResult<int>.Failure(decision.Reason ?? "Not permitted.", decision.RequirementId ?? "REC-001");
        }

        if (view.RunId is null)
        {
            return OperationResult<int>.Failure("There is no successful OCR run to reconcile.", "REC-001");
        }

        var difference = view.Differences.FirstOrDefault(d => d.FieldKey == request.FieldKey);
        if (difference is null)
        {
            return OperationResult<int>.Failure("That difference no longer exists; the draft patch is recomputed on every view.", "REC-001");
        }

        if (difference.IsResolved && difference.LatestFinding!.Decision == request.Decision)
        {
            return OperationResult<int>.Failure("That decision is already on record for this difference.", "REC-004");
        }

        // REC-009. A conflicted reading supports no decision that relies on one of its values.
        if (difference.IsConflicted && request.Decision is ReconciliationDecision.AppliedToDraftForm
                or ReconciliationDecision.InitiatePostAcceptanceCorrection or ReconciliationDecision.RecordMissingHistoricalEvent)
        {
            return OperationResult<int>.Failure(
                $"The verified readings of {difference.FieldKey} differ between pages ({string.Join(" / ", difference.ConflictingValues ?? [])}). Resolve which reading is the physical document's at verification first; nothing is applied or recorded on one of them.", "REC-009");
        }

        var warnings = new List<string>();
        switch (request.Decision)
        {
            case ReconciliationDecision.AppliedToDraftForm:
            {
                if (!view.IsPreAcceptance)
                {
                    return OperationResult<int>.Failure(
                        "The custodian has accepted this voucher: nothing is applied from a scan to an accepted record. Record a finding; a true error goes through AR 195-5 para 1-7c(3).", "REC-002");
                }

                if (difference.Applicability != DifferenceApplicability.AppliesToDraft)
                {
                    return OperationResult<int>.Failure(ApplicabilityRefusal(difference), difference.Applicability == DifferenceApplicability.NeverAppliedDocumentNumber ? "REC-005" : "REC-007");
                }

                if (!view.VerificationComplete)
                {
                    return OperationResult<int>.Failure(
                        $"{view.MandatoryVerificationsOutstanding} mandatory verification(s) are outstanding on this run. Nothing is applied from an unverified extraction.", "REC-002");
                }

                if (!difference.DocumentValueVerified)
                {
                    return OperationResult<int>.Failure("This field has not been verified by a person; nothing is applied from a raw extraction.", "REC-002");
                }

                var applied = await ApplyToDraftAsync(view, difference, ct);
                if (!applied.Succeeded)
                {
                    return OperationResult<int>.Failure(applied.Error!, applied.RequirementId);
                }

                warnings.AddRange(applied.Warnings);
                if (view.VoucherStatus == VoucherDerivedStatus.ReturnedForCorrection)
                {
                    warnings.Add("The voucher was returned under AR 195-5 2-3g: record the agent's correction of the PAPER form on the voucher page before resubmitting. The next submission is a new form revision.");
                }

                break;
            }

            case ReconciliationDecision.InitiatePostAcceptanceCorrection:
            {
                if (view.IsPreAcceptance)
                {
                    return OperationResult<int>.Failure("The voucher is not yet accepted; correct the draft instead (apply to the draft form).", "REC-002");
                }

                if (!view.CanInitiatePostAcceptanceCorrection)
                {
                    return OperationResult<int>.Failure(
                        "AR 195-5 para 1-7c(3): correcting an entry in the accepted accountability record is the custodian's act. It needs an active custodian appointment.", "IAM-005");
                }

                warnings.Add("Finding recorded. Complete the para 1-7c(3) correction on the item's history page: inform the supervisor, reference the MFR, and record the correction with this scan as its provenance. The record changes there, not here.");
                break;
            }

            case ReconciliationDecision.RecordMissingHistoricalEvent:
                warnings.Add(difference.Kind == ReconciliationDifferenceKind.CustodyRow
                    ? "Finding recorded. The custodian now records the custody row on the item's history through the custody workflow (REC-010) - naming the parties and the date the paper shows - with this scan as provenance. Nothing is recorded from the scan by itself; a release the paper shows goes through the release workflow."
                    : "Finding recorded. The event the scan shows is recorded through the workflow that owns it (custody, release, disposition), not from the scan.");
                break;
        }

        int findingId;
        try
        {
            var finding = new ReconciliationFinding(
                view.RunId.Value, view.SourceDocumentId, view.VoucherId, difference.EvidenceItemId, difference.Kind, difference.FieldKey,
                difference.CompanionValue, difference.DocumentValue, request.Decision, request.Narrative, _currentUser.UserId, _clock.UtcNow);
            _db.ReconciliationFindings.Add(finding);
            _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(ReconciliationFinding), null,
                previousValue: difference.CompanionValue, newValue: difference.DocumentValue,
                reason: $"{request.Decision} on {difference.FieldKey} of source document {view.SourceDocumentId} run {view.RunId} (voucher {view.VoucherIdentifier})");
            await _db.SaveChangesAsync(ct);
            findingId = finding.Id;
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        return OperationResult<int>.Success(findingId, warnings.ToArray());
    }

    private static string ApplicabilityRefusal(ReconciliationDifference d) => d.Applicability switch
    {
        DifferenceApplicability.NeverAppliedDocumentNumber => "A document number is never applied from a scan (REC-005). AR 195-5 2-4c: the custodian assigns it in the ledger and transcribes it, with attestation, on the voucher page.",
        DifferenceApplicability.NotAppliedCaseControlNumber => "A case control number is not applied from a scan: a voucher's case is changed on the case, not from a reading. Record a finding.",
        DifferenceApplicability.CustodyWorkflow => "A chain-of-custody row is recorded through the custody workflow, not applied to the form.",
        DifferenceApplicability.DispositionWorkflow => "A disposition entry is recorded through the disposition workflow (AR 195-5 2-8), not applied to the form.",
        DifferenceApplicability.ReviewOrWithdraw => "Nothing is removed from a form because a scan lacks it. If the line was entered in error on a returned form, withdraw it on the voucher page (VCH-026); otherwise flag it for review.",
        DifferenceApplicability.AcceptedRecordFindingOnly => "The voucher is accepted; findings only.",
        DifferenceApplicability.Conflicted => "The verified readings conflict between pages; resolve them at verification first.",
        _ => "This difference is not applied to the draft."
    };

    private async Task<OperationResult> ApplyToDraftAsync(ReconciliationView view, ReconciliationDifference difference, CancellationToken ct)
    {
        var voucher = await _reads.GetVoucherAsync(view.VoucherId, ct);
        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "REC-001");
        }

        OperationResult result;
        switch (difference.Kind)
        {
            case ReconciliationDifferenceKind.HeaderField:
            {
                var field = OcrFieldCatalog.FieldName(difference.FieldKey);
                var value = difference.DocumentValue ?? string.Empty;
                result = await _vouchers.UpdateHeaderAsync(new UpdateVoucherHeaderRequest(
                    voucher.Id,
                    field == "ReceivingActivity" ? value : voucher.ReceivingActivity,
                    field == "Location" ? value : voucher.ReceivingActivityLocation,
                    field == "ReceivedFromName" ? value : voucher.ReceivedFrom), ct);
                break;
            }

            case ReconciliationDifferenceKind.ItemField:
            {
                // ONE raw field, through the typed patch (ITEM-008). The rendered description on
                // the form carries the POSSIBLE BIOHAZARD annotation; the raw description does not.
                var field = OcrFieldCatalog.FieldName(difference.FieldKey);
                var value = difference.DocumentValue;
                var patch = field switch
                {
                    OcrFieldCatalog.ItemDescriptionField => new UpdateDraftItemFieldRequest(difference.EvidenceItemId!.Value, DraftItemField.Description, EvidenceItem.RawDescriptionFromForm(value ?? string.Empty)),
                    OcrFieldCatalog.ItemQuantityField => new UpdateDraftItemFieldRequest(difference.EvidenceItemId!.Value, DraftItemField.Quantity, value),
                    OcrFieldCatalog.ItemSerialNumberField => new UpdateDraftItemFieldRequest(difference.EvidenceItemId!.Value, DraftItemField.SerialNumber, value),
                    OcrFieldCatalog.ItemUniqueDeviceIdentifierField => new UpdateDraftItemFieldRequest(difference.EvidenceItemId!.Value, DraftItemField.UniqueDeviceIdentifier, value),
                    _ => null
                };
                if (patch is null)
                {
                    return OperationResult.Failure($"{field} is not an item field that reconciliation applies.", "REC-007");
                }

                result = await _vouchers.UpdateDraftItemFieldAsync(patch, ct);
                break;
            }

            case ReconciliationDifferenceKind.MissingItem:
            {
                // The scan's item row becomes a new line at the next item number. Item numbers
                // are contiguous (I-01); a scan row numbered out of sequence is a finding, not a line.
                var next = voucher.Items.Count(i => !i.IsWithdrawnFromForm) + 1;
                if (difference.ItemNumber != next)
                {
                    return OperationResult.Failure($"The scan's item {difference.ItemNumber} would not be the next item number ({next}); item numbers are contiguous. Flag it for review.", "ITEM-001");
                }

                var parts = ParseItemRow(difference.DocumentValue);
                var added = await _vouchers.AddItemAsync(new AddItemRequest(voucher.Id, EvidenceItem.RawDescriptionFromForm(parts.Description), parts.Quantity, parts.Serial, parts.Udi, false, false, false, null), ct);
                result = added.Succeeded ? OperationResult.Success(added.Warnings.ToArray()) : OperationResult.Failure(added.Error!, added.RequirementId);
                break;
            }

            default:
                return OperationResult.Failure(ApplicabilityRefusal(difference), "REC-007");
        }

        if (!result.Succeeded)
        {
            return result;
        }

        // REC-007: "applied" is only ever recorded when the companion now says what the scan
        // says. Recompute and check, rather than trusting that the path changed the field.
        var after = await _reads.GetVoucherAsync(view.VoucherId, ct);
        var status = await _ocr.GetStatusAsync(view.SourceDocumentId, ct);
        var remaining = after is null || status?.LatestRun is null ? null : Compute(after, status.LatestRun, []).FirstOrDefault(d => d.FieldKey == difference.FieldKey);
        if (remaining is not null && remaining.Kind == difference.Kind)
        {
            return OperationResult.Failure(
                $"The change did not bring the companion record into agreement with the verified scan for {difference.FieldKey} (it now reads \"{remaining.CompanionValue}\"). Nothing is recorded as applied.", "REC-007");
        }

        return result;
    }

    // ---- the draft patch ----------------------------------------------------------------

    internal static IReadOnlyList<ReconciliationDifference> Compute(VoucherDetailView voucher, OcrRunView run, IReadOnlyList<ReconciliationFindingRow> findings, IReadOnlyDictionary<int, int>? recordedFromFindings = null)
    {
        var result = new List<ReconciliationDifference>();
        recordedFromFindings ??= new Dictionary<int, int>();
        var preAcceptance = voucher.AllowsItemEditing;

        // REC-008. A finding resolves a difference only when it was taken on THIS run, about
        // THIS kind and item, comparing THESE two values.
        ReconciliationFindingRow? Latest(string key, ReconciliationDifferenceKind kind, int? itemId, string? companion, string? document)
            => findings.Where(f => f.OcrRunId == run.RunId && f.FieldKey == key && f.Kind == kind && f.EvidenceItemId == itemId
                                   && Same(f.CompanionValue, companion) && Same(f.DocumentValue, document))
                .OrderByDescending(f => f.DecidedAtUtc).ThenByDescending(f => f.Id).FirstOrDefault();

        // A logical field's value for reconciliation, across every page it appears on
        // (REC-009): all verified occurrences must agree. NotApplicable occurrences are set
        // aside by the verifier; that is how a person says which reading is the document's.
        (string? Value, bool Verified, IReadOnlyList<string>? Conflicts) Read(string key)
        {
            var occurrences = run.Fields.Where(x => x.FieldKey == key).OrderBy(x => x.PageNumber).ToList();
            if (occurrences.Count == 0) return (null, false, null);

            var applicable = occurrences.Where(o => o.Current is null || o.Current.Decision != FieldVerificationDecision.NotApplicable).ToList();
            if (applicable.Count == 0) return (null, false, null);

            var verified = applicable.Where(o => o.Current is not null).Select(o => o.VerifiedValue).ToList();
            var distinctVerified = verified.Where(v => v is not null).Select(v => v!).GroupBy(Canonical).Select(g => g.First()).ToList();
            if (distinctVerified.Count > 1)
            {
                return (string.Join(" ‖ ", distinctVerified), true, distinctVerified);
            }

            var allVerified = applicable.All(o => o.Current is not null);
            if (allVerified)
            {
                return (distinctVerified.FirstOrDefault(), true, null);
            }

            var first = applicable.First();
            var candidate = distinctVerified.FirstOrDefault() ?? first.NormalizedCandidate ?? (first.RawText.Length == 0 ? null : first.RawText);
            return (candidate, false, null);
        }

        void Add(string key, ReconciliationDifferenceKind kind, int? itemId, int? itemNumber, string? companion, (string? Value, bool Verified, IReadOnlyList<string>? Conflicts) read, DifferenceApplicability applicability, string explanation)
        {
            if (read.Conflicts is null && Same(companion, read.Value)) return;
            var app = read.Conflicts is not null ? DifferenceApplicability.Conflicted
                : applicability == DifferenceApplicability.AppliesToDraft && !preAcceptance ? DifferenceApplicability.AcceptedRecordFindingOnly
                : applicability;
            var explain = read.Conflicts is null ? explanation : $"{explanation} The verified readings differ between pages: {string.Join(" / ", read.Conflicts)}. Resolve at verification (mark the reading that is not the document's as not applicable, or correct it).";
            result.Add(new ReconciliationDifference(key, kind, itemId, itemNumber, companion, read.Value, read.Verified, app, explain, Latest(key, kind, itemId, companion, read.Value), read.Conflicts));
        }

        // Header.
        var ra = Read(OcrFieldCatalog.ReceivingActivity);
        if (ra.Value is not null) Add(OcrFieldCatalog.ReceivingActivity, ReconciliationDifferenceKind.HeaderField, null, null, voucher.ReceivingActivity, ra, DifferenceApplicability.AppliesToDraft, "Receiving activity on the form vs the companion record.");
        var loc = Read(OcrFieldCatalog.Location);
        if (loc.Value is not null) Add(OcrFieldCatalog.Location, ReconciliationDifferenceKind.HeaderField, null, null, voucher.ReceivingActivityLocation, loc, DifferenceApplicability.AppliesToDraft, "Location on the form vs the companion record.");
        var from = Read(OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived);
        if (from.Value is not null) Add(OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived, ReconciliationDifferenceKind.HeaderField, null, null, voucher.ReceivedFrom, from, DifferenceApplicability.AppliesToDraft, "Person from whom received on the form vs the companion record.");
        var ccn = Read(OcrFieldCatalog.CaseControlNumber);
        if (ccn.Value is not null) Add(OcrFieldCatalog.CaseControlNumber, ReconciliationDifferenceKind.HeaderField, null, null, voucher.CaseControlNumber, ccn, DifferenceApplicability.NotAppliedCaseControlNumber, "Case control number on the form vs the case the voucher belongs to. Not applied here: a voucher's case is changed on the case, not from a scan.");

        // Document number: compared, never applied (REC-005).
        var docNo = Read(OcrFieldCatalog.DocumentNumber);
        var recorded = voucher.DocumentNumbers.Where(n => n.IsCurrent).Select(n => n.DocumentNumber).FirstOrDefault()
                       ?? voucher.DocumentNumbers.OrderByDescending(n => n.EnteredAtUtc).Select(n => n.DocumentNumber).FirstOrDefault();
        if (docNo.Value is not null || recorded is not null)
        {
            Add(OcrFieldCatalog.DocumentNumber, ReconciliationDifferenceKind.DocumentNumber, null, null, recorded, docNo, DifferenceApplicability.NeverAppliedDocumentNumber,
                recorded is null
                    ? "The form shows a document number; none is recorded. The custodian transcribes it, with attestation, on the voucher page (AR 195-5 2-4c). It is never applied from a scan."
                    : "The document number on the form differs from the one recorded. A misread is corrected at verification; a real difference is a custodian matter. Never applied from a scan.");
        }

        // Items, by item number.
        var lines = voucher.Items.Where(i => !i.IsWithdrawnFromForm).ToDictionary(i => i.ItemNumber);
        var scanItems = run.Fields.Where(f => f.FieldKey.StartsWith("Item[", StringComparison.Ordinal))
            .GroupBy(f => int.Parse(f.FieldKey[5..f.FieldKey.IndexOf(']', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key).ToList();
        foreach (var group in scanItems)
        {
            var n = group.Key;
            var numberText = Read($"Item[{n}].{OcrFieldCatalog.ItemNumberField}");
            var itemNumber = int.TryParse(numberText.Value, out var parsed) ? parsed : n;
            var description = Read($"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}");
            var quantity = Read($"Item[{n}].{OcrFieldCatalog.ItemQuantityField}");
            var serial = Read($"Item[{n}].{OcrFieldCatalog.ItemSerialNumberField}");
            var udi = Read($"Item[{n}].{OcrFieldCatalog.ItemUniqueDeviceIdentifierField}");

            if (!lines.TryGetValue(itemNumber, out var line))
            {
                var allVerified = description.Verified && (quantity.Value is null || quantity.Verified) && (serial.Value is null || serial.Verified) && (udi.Value is null || udi.Verified);
                var key = $"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}";
                var value = FormatItemRow(description.Value, quantity.Value, serial.Value, udi.Value);
                var conflicted = description.Conflicts is not null || quantity.Conflicts is not null || serial.Conflicts is not null || udi.Conflicts is not null;
                result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.MissingItem, null, itemNumber, null, value, allVerified,
                    conflicted ? DifferenceApplicability.Conflicted : preAcceptance ? DifferenceApplicability.AppliesToDraft : DifferenceApplicability.AcceptedRecordFindingOnly,
                    $"Item {itemNumber} is on the form and not on the companion record.", Latest(key, ReconciliationDifferenceKind.MissingItem, null, null, value),
                    conflicted ? [description.Value ?? "", quantity.Value ?? "", serial.Value ?? "", udi.Value ?? ""] : null));
                continue;
            }

            if (description.Value is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.DescriptionForForm, description, DifferenceApplicability.AppliesToDraft, $"Item {itemNumber} description (the form shows the rendered text; applying stores the raw description).");
            if (quantity.Value is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemQuantityField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.Quantity, quantity, DifferenceApplicability.AppliesToDraft, $"Item {itemNumber} quantity.");
            if (serial.Value is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemSerialNumberField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.SerialNumber, serial, DifferenceApplicability.AppliesToDraft, $"Item {itemNumber} serial number.");
            if (udi.Value is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemUniqueDeviceIdentifierField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.UniqueDeviceIdentifier, udi, DifferenceApplicability.AppliesToDraft, $"Item {itemNumber} device identifier.");
        }

        if (scanItems.Count > 0)
        {
            var scanned = scanItems.Select(g => int.TryParse(Read($"Item[{g.Key}].{OcrFieldCatalog.ItemNumberField}").Value, out var p) ? p : g.Key).ToHashSet();
            foreach (var extra in lines.Values.Where(l => !scanned.Contains(l.ItemNumber)))
            {
                var key = $"Item[{extra.ItemNumber}].{OcrFieldCatalog.ItemDescriptionField}";
                if (result.Any(r => r.FieldKey == key)) continue;
                result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.ExtraItem, extra.Id, extra.ItemNumber, extra.DescriptionForForm, null, true, DifferenceApplicability.ReviewOrWithdraw,
                    $"Item {extra.ItemNumber} is on the companion record and not on the form (or was not read). Nothing is removed from a scan; if the line was entered in error on a returned form, withdraw it on the voucher page (VCH-026).",
                    Latest(key, ReconciliationDifferenceKind.ExtraItem, extra.Id, extra.DescriptionForForm, null)));
            }
        }

        // Chain of custody rows: the custody workflow's, offered for a decision here.
        var custody = run.Fields.Where(f => f.FieldKey.StartsWith("Custody[", StringComparison.Ordinal))
            .GroupBy(f => int.Parse(f.FieldKey[8..f.FieldKey.IndexOf(']', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key);
        foreach (var row in custody)
        {
            var k = row.Key;
            var parts = new[] { OcrFieldCatalog.CustodyItemNumberField, OcrFieldCatalog.CustodyDateField, OcrFieldCatalog.CustodyReleasedByNameField, OcrFieldCatalog.CustodyReceivedByNameField, OcrFieldCatalog.CustodyPurposeField }
                .Select(p => Read($"Custody[{k}].{p}")).ToList();
            if (parts.All(p => p.Value is null)) continue;
            var key = $"Custody[{k}].{OcrFieldCatalog.CustodyDateField}";
            var value = string.Join(" | ", parts.Select(p => p.Value ?? "—"));
            var conflicted = parts.Any(p => p.Conflicts is not null);
            var latestCustodyFinding = Latest(key, ReconciliationDifferenceKind.CustodyRow, null, null, value);
            result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.CustodyRow, null, null, null, value,
                parts.Where(p => p.Value is not null).All(p => p.Verified),
                conflicted ? DifferenceApplicability.Conflicted : DifferenceApplicability.CustodyWorkflow,
                $"Chain of custody row {k} on the form: item(s) | date | released by | received by | purpose. A row the companion lacks is recorded by the custodian through the custody workflow (REC-010), never from the scan by itself.",
                latestCustodyFinding, conflicted ? parts.SelectMany(p => p.Conflicts ?? []).ToList() : null,
                latestCustodyFinding is not null && recordedFromFindings.TryGetValue(latestCustodyFinding.Id, out var recordedEventId) ? recordedEventId : null,
                int.TryParse((parts[0].Value ?? string.Empty).Trim(), out var custodyItemNumber) && lines.TryGetValue(custodyItemNumber, out var custodyLine) ? custodyLine.Id : null));
        }

        // Disposition blocks.
        foreach (var key in new[] { OcrFieldCatalog.DispositionAction, OcrFieldCatalog.DispositionAuthority })
        {
            var read = Read(key);
            if (read.Value is null) continue;
            result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.Disposition, null, null, null, read.Value, read.Verified,
                read.Conflicts is not null ? DifferenceApplicability.Conflicted : DifferenceApplicability.DispositionWorkflow,
                "A final disposal entry on the form. Disposition is recorded through the disposition workflow (AR 195-5 2-8), not from a scan.",
                Latest(key, ReconciliationDifferenceKind.Disposition, null, null, read.Value), read.Conflicts));
        }

        return result;
    }

    private static bool Same(string? a, string? b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    private static string Canonical(string? s)
        => s is null ? string.Empty : System.Text.RegularExpressions.Regex.Replace(s.Trim().ToUpperInvariant(), @"\s+", " ");

    private static string FormatItemRow(string? description, string? quantity, string? serial, string? udi)
        => string.Join(" | ", new[] { description ?? "—", quantity ?? "—", serial ?? "—", udi ?? "—" });

    private static (string Description, string? Quantity, string? Serial, string? Udi) ParseItemRow(string? row)
    {
        var parts = (row ?? string.Empty).Split(" | ");
        string? Part(int i) => parts.Length > i && parts[i] != "—" ? parts[i] : null;
        return (Part(0) ?? "(description not read)", Part(1), Part(2), Part(3));
    }

    private async Task<IReadOnlyList<ReconciliationFindingRow>> FindingsAsync(int sourceDocumentId, CancellationToken ct)
    {
        var rows = await _db.ReconciliationFindings.AsNoTracking().Where(f => f.SourceDocumentId == sourceDocumentId)
            .OrderBy(f => f.DecidedAtUtc).ThenBy(f => f.Id).ToListAsync(ct);
        var userIds = rows.Select(r => r.DecidedByUserId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);
        return rows.Select(f => new ReconciliationFindingRow(f.Id, f.OcrRunId, f.FieldKey, f.Kind, f.EvidenceItemId, f.CompanionValue, f.DocumentValue, f.Decision, f.Narrative,
            names.TryGetValue(f.DecidedByUserId, out var n) ? n : "(unknown user)", f.DecidedAtUtc)).ToList();
    }
}
