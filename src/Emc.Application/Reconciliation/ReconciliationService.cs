using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Ocr;
using Emc.Application.Reads;
using Emc.Domain.Common;
using Emc.Domain.Ocr;
using Emc.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Reconciliation;

/// <summary>One difference between the verified scan and the companion record, as computed now. Not stored: the draft patch is recomputed on every view (REC-001).</summary>
public sealed record ReconciliationDifference(
    string FieldKey,
    ReconciliationDifferenceKind Kind,
    int? EvidenceItemId,
    int? ItemNumber,
    string? CompanionValue,
    string? DocumentValue,
    bool DocumentValueVerified,
    string Explanation,
    ReconciliationFindingRow? LatestFinding)
{
    public bool IsResolved => LatestFinding is not null;
}

public sealed record ReconciliationFindingRow(
    int Id, string FieldKey, ReconciliationDifferenceKind Kind, int? EvidenceItemId, string? CompanionValue, string? DocumentValue,
    ReconciliationDecision Decision, string? Narrative, string DecidedByName, DateTimeOffset DecidedAtUtc);

public sealed record ReconciliationView(
    int SourceDocumentId,
    int EvidenceRoomId,
    int VoucherId,
    string VoucherIdentifier,
    Emc.Domain.Common.VoucherDerivedStatus VoucherStatus,
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
}

public sealed record ReconciliationDecisionRequest(int SourceDocumentId, string FieldKey, ReconciliationDecision Decision, string? Narrative);

/// <summary>
/// REC-001 to REC-004. Reconciliation is the ONLY place a verified scan meets the companion
/// record, and it is explicit: a person sees each difference and decides. Before acceptance the
/// decision "applied to draft form" changes the draft through the ordinary voucher service (so a
/// returned form's resubmission is a new revision, VCH-025). After acceptance nothing is applied:
/// a finding is recorded, and a true error in the accepted record is taken to the para 1-7c(3)
/// correction workflow, where the correction event carries this scan as its provenance.
/// A document number is never applied from a scan (REC-005).
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
        var differences = run is null ? [] : Compute(voucher, run, findings);
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
                if (view.VoucherStatus == Emc.Domain.Common.VoucherDerivedStatus.ReturnedForCorrection)
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
                warnings.Add("Finding recorded. The event the scan shows is recorded through the workflow that owns it (custody, release, disposition), not from the scan.");
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
                reason: $"{request.Decision} on {difference.FieldKey} of source document {view.SourceDocumentId} (voucher {view.VoucherIdentifier})");
            await _db.SaveChangesAsync(ct);
            findingId = finding.Id;
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        return OperationResult<int>.Success(findingId, warnings.ToArray());
    }

    private async Task<OperationResult> ApplyToDraftAsync(ReconciliationView view, ReconciliationDifference difference, CancellationToken ct)
    {
        var voucher = await _reads.GetVoucherAsync(view.VoucherId, ct);
        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "REC-001");
        }

        switch (difference.Kind)
        {
            case ReconciliationDifferenceKind.DocumentNumber:
                return OperationResult.Failure(
                    "A document number is never applied from a scan (REC-005). AR 195-5 2-4c: the custodian assigns it in the ledger and transcribes it, with attestation, on the voucher page.", "REC-005");

            case ReconciliationDifferenceKind.HeaderField:
            {
                var field = OcrFieldCatalog.FieldName(difference.FieldKey);
                var value = difference.DocumentValue ?? string.Empty;
                return await _vouchers.UpdateHeaderAsync(new UpdateVoucherHeaderRequest(
                    voucher.Id,
                    field == "ReceivingActivity" ? value : voucher.ReceivingActivity,
                    field == "Location" ? value : voucher.ReceivingActivityLocation,
                    field == "ReceivedFromName" ? value : voucher.ReceivedFrom), ct);
            }

            case ReconciliationDifferenceKind.ItemField:
            {
                var item = voucher.Items.First(i => i.Id == difference.EvidenceItemId);
                var field = OcrFieldCatalog.FieldName(difference.FieldKey);
                var value = difference.DocumentValue;
                return await _vouchers.UpdateItemAsync(new UpdateItemRequest(
                    item.Id,
                    field == OcrFieldCatalog.ItemDescriptionField ? value ?? item.DescriptionForForm : item.DescriptionForForm,
                    field == OcrFieldCatalog.ItemQuantityField ? value : item.Quantity,
                    field == OcrFieldCatalog.ItemSerialNumberField ? value : item.SerialNumber,
                    field == OcrFieldCatalog.ItemUniqueDeviceIdentifierField ? value : item.UniqueDeviceIdentifier,
                    item.IsPossibleBiohazard, false, item.IsSealed, null), ct);
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
                var added = await _vouchers.AddItemAsync(new AddItemRequest(voucher.Id, parts.Description, parts.Quantity, parts.Serial, parts.Udi, false, false, false, null), ct);
                return added.Succeeded ? OperationResult.Success(added.Warnings.ToArray()) : OperationResult.Failure(added.Error!, added.RequirementId);
            }

            default:
                return OperationResult.Failure($"A {difference.Kind} difference is not applied to a draft; it is a finding.", "REC-002");
        }
    }

    // ---- the draft patch ----------------------------------------------------------------

    internal static IReadOnlyList<ReconciliationDifference> Compute(VoucherDetailView voucher, OcrRunView run, IReadOnlyList<ReconciliationFindingRow> findings)
    {
        var result = new List<ReconciliationDifference>();
        ReconciliationFindingRow? Latest(string key) => findings.Where(f => f.FieldKey == key).OrderByDescending(f => f.DecidedAtUtc).ThenByDescending(f => f.Id).FirstOrDefault();

        // A field's value for reconciliation is the VERIFIED value; an unverified field contributes
        // its candidate for display only, marked unverified, and can never be applied.
        (string? Value, bool Verified) Read(string key)
        {
            var f = run.Fields.Where(x => x.FieldKey == key).OrderBy(x => x.PageNumber).FirstOrDefault();
            if (f is null) return (null, false);
            if (f.Current is not null) return (f.VerifiedValue, f.Current.Decision != FieldVerificationDecision.NotApplicable);
            return (f.NormalizedCandidate ?? (f.RawText.Length == 0 ? null : f.RawText), false);
        }

        void Add(string key, ReconciliationDifferenceKind kind, int? itemId, int? itemNumber, string? companion, string? document, bool verified, string explanation)
        {
            if (Same(companion, document)) return;
            result.Add(new ReconciliationDifference(key, kind, itemId, itemNumber, companion, document, verified, explanation, Latest(key)));
        }

        // Header.
        var (ra, raV) = Read(OcrFieldCatalog.ReceivingActivity);
        if (ra is not null) Add(OcrFieldCatalog.ReceivingActivity, ReconciliationDifferenceKind.HeaderField, null, null, voucher.ReceivingActivity, ra, raV, "Receiving activity on the form vs the companion record.");
        var (loc, locV) = Read(OcrFieldCatalog.Location);
        if (loc is not null) Add(OcrFieldCatalog.Location, ReconciliationDifferenceKind.HeaderField, null, null, voucher.ReceivingActivityLocation, loc, locV, "Location on the form vs the companion record.");
        var (from, fromV) = Read(OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived);
        if (from is not null) Add(OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived, ReconciliationDifferenceKind.HeaderField, null, null, voucher.ReceivedFrom, from, fromV, "Person from whom received on the form vs the companion record.");
        var (ccn, ccnV) = Read(OcrFieldCatalog.CaseControlNumber);
        if (ccn is not null) Add(OcrFieldCatalog.CaseControlNumber, ReconciliationDifferenceKind.HeaderField, null, null, voucher.CaseControlNumber, ccn, ccnV, "Case control number on the form vs the case the voucher belongs to. Not applied here: a voucher's case is changed on the case, not from a scan.");

        // Document number: compared, never applied (REC-005).
        var (docNo, docNoV) = Read(OcrFieldCatalog.DocumentNumber);
        var recorded = voucher.DocumentNumbers.Where(n => n.IsCurrent).Select(n => n.DocumentNumber).FirstOrDefault()
                       ?? voucher.DocumentNumbers.OrderByDescending(n => n.EnteredAtUtc).Select(n => n.DocumentNumber).FirstOrDefault();
        if (docNo is not null || recorded is not null)
        {
            Add(OcrFieldCatalog.DocumentNumber, ReconciliationDifferenceKind.DocumentNumber, null, null, recorded, docNo, docNoV,
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
            var (numberText, _) = Read($"Item[{n}].{OcrFieldCatalog.ItemNumberField}");
            var itemNumber = int.TryParse(numberText, out var parsed) ? parsed : n;
            var (description, descriptionV) = Read($"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}");
            var (quantity, quantityV) = Read($"Item[{n}].{OcrFieldCatalog.ItemQuantityField}");
            var (serial, serialV) = Read($"Item[{n}].{OcrFieldCatalog.ItemSerialNumberField}");
            var (udi, udiV) = Read($"Item[{n}].{OcrFieldCatalog.ItemUniqueDeviceIdentifierField}");

            if (!lines.TryGetValue(itemNumber, out var line))
            {
                var allVerified = descriptionV && (quantity is null || quantityV) && (serial is null || serialV) && (udi is null || udiV);
                result.Add(new ReconciliationDifference($"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}", ReconciliationDifferenceKind.MissingItem, null, itemNumber, null,
                    FormatItemRow(description, quantity, serial, udi), allVerified, $"Item {itemNumber} is on the form and not on the companion record.", Latest($"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}")));
                continue;
            }

            if (description is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemDescriptionField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.DescriptionForForm, description, descriptionV, $"Item {itemNumber} description.");
            if (quantity is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemQuantityField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.Quantity, quantity, quantityV, $"Item {itemNumber} quantity.");
            if (serial is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemSerialNumberField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.SerialNumber, serial, serialV, $"Item {itemNumber} serial number.");
            if (udi is not null) Add($"Item[{n}].{OcrFieldCatalog.ItemUniqueDeviceIdentifierField}", ReconciliationDifferenceKind.ItemField, line.Id, itemNumber, line.UniqueDeviceIdentifier, udi, udiV, $"Item {itemNumber} device identifier.");
        }

        if (scanItems.Count > 0)
        {
            var scanned = scanItems.Select(g => int.TryParse(Read($"Item[{g.Key}].{OcrFieldCatalog.ItemNumberField}").Value, out var p) ? p : g.Key).ToHashSet();
            foreach (var extra in lines.Values.Where(l => !scanned.Contains(l.ItemNumber)))
            {
                var key = $"Item[{extra.ItemNumber}].{OcrFieldCatalog.ItemDescriptionField}";
                if (result.Any(r => r.FieldKey == key)) continue;
                result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.ExtraItem, extra.Id, extra.ItemNumber, extra.DescriptionForForm, null, true,
                    $"Item {extra.ItemNumber} is on the companion record and not on the form (or was not read). Nothing is removed from a scan; if the line was entered in error on a returned form, withdraw it on the voucher page (VCH-026).", Latest(key)));
            }
        }

        // Chain of custody rows. The companion record has no custody-recording workflow in this
        // version, so every row on the form is presented for a decision; the ordinary decision is
        // "record missing historical event" (for the custody workflow) or "already correct" once it is.
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
            result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.CustodyRow, null, null, null,
                string.Join(" | ", parts.Select(p => p.Value ?? "—")), parts.Where(p => p.Value is not null).All(p => p.Verified),
                $"Chain of custody row {k} on the form: item(s) | date | released by | received by | purpose. Custody events are recorded through the custody workflow, not from a scan.", Latest(key)));
        }

        // Disposition blocks.
        foreach (var key in new[] { OcrFieldCatalog.DispositionAction, OcrFieldCatalog.DispositionAuthority })
        {
            var (value, verified) = Read(key);
            if (value is null) continue;
            result.Add(new ReconciliationDifference(key, ReconciliationDifferenceKind.Disposition, null, null, null, value, verified,
                "A final disposal entry on the form. Disposition is recorded through the disposition workflow (AR 195-5 2-8), not from a scan.", Latest(key)));
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
        return rows.Select(f => new ReconciliationFindingRow(f.Id, f.FieldKey, f.Kind, f.EvidenceItemId, f.CompanionValue, f.DocumentValue, f.Decision, f.Narrative,
            names.TryGetValue(f.DecidedByUserId, out var n) ? n : "(unknown user)", f.DecidedAtUtc)).ToList();
    }
}
