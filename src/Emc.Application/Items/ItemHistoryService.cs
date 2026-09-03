using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Items;

/// <summary>
/// One row of an item's chronological history.
///
/// <paramref name="CorrectedFieldNames"/> drives the UI's "Corrected" marker. AR 195-5 2-5b(5)
/// requires a struck-through ledger entry to remain READABLE, so a corrected event is never
/// hidden - it is marked field by field, it keeps every value that was not corrected, and the
/// correction that changed it is linked (AUD-006).
/// </summary>
public sealed record ItemHistoryRow(
    int EventId,
    int SequenceNumber,
    ItemEventKind Kind,
    DateTimeOffset OccurredAtLocal,
    DateTimeOffset RecordedAtUtc,
    string RecordedByName,
    string Summary,
    string? Notes,

    /// <summary>Fields of this event that a correction has changed. Empty for most rows.</summary>
    IReadOnlyCollection<string> CorrectedFieldNames,

    /// <summary>Each correctable field with the value the record NOW reads (AUD-015).</summary>
    IReadOnlyDictionary<string, string?> EffectiveFields,

    int? CorrectsEventId,
    string? CorrectionFieldName,
    string? CorrectionOriginalValue,
    string? CorrectionNewValue,
    string? CorrectionReason,
    string? CorrectionMfrReference,
    CorrectionCategory? CorrectionCategory,
    bool CorrectionSatisfies1_7c3,

    /// <summary>Set on a correction row that replaced a ROW rather than free text (AUD-016).</summary>
    CorrectableFieldReference CorrectionReferenceKind = CorrectableFieldReference.None,
    int? CorrectionOriginalReferenceId = null,
    int? CorrectionNewReferenceId = null,

    /// <summary>
    /// Which of this event's correctable fields name a ROW, and what kind. Lets the correction
    /// form offer the right control - a picker rather than a text box - instead of relying on the
    /// server to reject free text after the fact (AUD-016).
    /// </summary>
    IReadOnlyDictionary<string, CorrectableFieldReference>? ReferenceFieldKinds = null,

    /// <summary>
    /// What this correction actually changed: the field as it read immediately before it. Equal
    /// to the original value only for the first correction to a field (AUD-017).
    /// </summary>
    string? CorrectionPreviousValue = null,
    bool CorrectionCorrectsTheOriginalEntry = true)
{
    /// <summary>True when any field of this event has been corrected.</summary>
    public bool HasCorrections => CorrectedFieldNames.Count > 0;
}

public sealed record ItemHistoryView(
    int ItemId,
    int ItemNumber,
    string VoucherIdentifier,
    string CaseControlNumber,
    string DescriptionForForm,
    AccountabilityStatus AccountabilityStatus,
    string? CurrentLocationPath,
    string? CurrentCustodyHolder,
    IReadOnlyList<ItemHistoryRow> History,
    ChainVerificationResult ChainVerification,

    /// <summary>
    /// The storage location and custody party the record now names, as ROWS. Carried alongside
    /// the display text because a correction must move both, and because inventory and
    /// discrepancy work resolve items by identifier (AUD-016).
    /// </summary>
    int? CurrentLocationId = null,
    int? CurrentCustodyHolderPartyId = null);

/// <summary>
/// A correction request.
///
/// Note what is ABSENT: the original value. The server derives it from the stored event
/// (AUD-014). An "original value" supplied by the client could be anything the corrector chose,
/// which would make the audit record worthless.
///
/// For a field that names a ROW - a storage location, a custody party - the request supplies
/// <paramref name="CorrectedReferenceId"/> and NOT <paramref name="CorrectedValue"/>. The display
/// text is read from the replacement row by the server, so the recorded text and the recorded
/// identifier cannot disagree (AUD-016). For a free-text field it is the other way round.
/// </summary>
public sealed record RecordCorrectionRequest(
    int CorrectedEventId,
    string FieldName,
    string? CorrectedValue,
    string Reason,
    CorrectionCategory Category,
    string? MfrReference,
    int? SupervisorNotifiedUserId,
    DateTimeOffset? SupervisorNotifiedAtUtc,
    int? CorrectedReferenceId = null);

public interface IItemHistoryService
{
    Task<ItemHistoryView?> GetAsync(int itemId, CancellationToken ct = default);
    Task<OperationResult> RecordCorrectionAsync(RecordCorrectionRequest request, CancellationToken ct = default);
}

/// <summary>
/// The item history read model, and the correction workflow.
///
/// The history is a single ordered query over one table because all event kinds share it
/// (docs/architecture.md §4.1), and it includes superseded events. AR 195-5's model for an
/// erroneous entry is a single line through it "so it may still be read" (2-5b(5)) — not
/// removal — and the software analogue is the same: mark it, link the correction, never hide it.
/// </summary>
public sealed class ItemHistoryService : IItemHistoryService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IItemEventRecorder _events;
    private readonly IClock _clock;

    public ItemHistoryService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        ICurrentUser currentUser,
        IAuditRecorder audit,
        IItemEventRecorder events,
        IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _events = events;
        _clock = clock;
    }

    public async Task<ItemHistoryView?> GetAsync(int itemId, CancellationToken ct = default)
    {
        // IAM-017 / IAM-018. Item history contains evidence descriptions, serial numbers, unique
        // device identifiers, custody parties and locations - the most sensitive content in the
        // application. Authorize on the owning evidence room BEFORE any of it is read, and report
        // an unauthorized item as ABSENT so identifiers cannot be enumerated.
        var owningRoomId = await _db.EvidenceItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => (int?)i.Voucher!.EvidenceRoomId)
            .FirstOrDefaultAsync(ct);

        if (owningRoomId is null)
        {
            return null;
        }

        if (!(await _authorization.AuthorizeAsync(
                EmcPermissions.ViewEvidenceHistory, owningRoomId, ct)).IsAllowed)
        {
            return null;
        }

        var item = await _db.EvidenceItems
            .AsNoTracking()
            .Include(i => i.Voucher!).ThenInclude(v => v.Case)
            .Include(i => i.Voucher!).ThenInclude(v => v.DocumentNumberAssignments)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item?.Voucher is null)
        {
            return null;
        }

        var events = await _db.ItemEvents
            .AsNoTracking()
            .Where(e => e.EvidenceItemId == itemId)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.SequenceNumber)
            .ToListAsync(ct);

        var userNames = await ResolveUserNamesAsync(events, ct);

        // AUD-015. Effective values are computed once and attached to each row, so the page can
        // show what the record now reads without re-deriving it.
        var corrections = events.OfType<CorrectionEvent>().ToList();

        var rows = events.Select(e =>
            {
                var effective = new EffectiveItemEvent(e, corrections);
                var correction = e as CorrectionEvent;

                return new ItemHistoryRow(
                    EventId: e.Id,
                    SequenceNumber: e.SequenceNumber,
                    Kind: e.Kind,
                    OccurredAtLocal: e.OccurredAtLocal,
                    RecordedAtUtc: e.RecordedAtUtc,
                    RecordedByName: userNames.GetValueOrDefault(e.RecordedByUserId, "(unknown user)"),
                    Summary: e.Summarize(),
                    Notes: e.Notes,
                    CorrectedFieldNames: effective.CorrectedFieldNames,
                    EffectiveFields: effective.EffectiveFields,
                    CorrectsEventId: correction?.CorrectsEventId,
                    CorrectionFieldName: correction?.FieldName,
                    CorrectionOriginalValue: correction?.OriginalValue,
                    CorrectionNewValue: correction?.CorrectedValue,
                    CorrectionReason: correction?.Reason,
                    CorrectionMfrReference: correction?.MfrReference,
                    CorrectionCategory: correction?.Category,
                    CorrectionSatisfies1_7c3: correction?.SatisfiesParagraph1_7c3 ?? true,
                    CorrectionReferenceKind:
                        correction?.ReferenceKind ?? CorrectableFieldReference.None,
                    CorrectionOriginalReferenceId: correction?.OriginalReferenceId,
                    CorrectionNewReferenceId: correction?.CorrectedReferenceId,
                    ReferenceFieldKinds: e.ReferenceFields.ToDictionary(
                        f => f.Key, f => f.Value.Kind, StringComparer.Ordinal),
                    CorrectionPreviousValue: correction?.PreviousEffectiveValue,
                    CorrectionCorrectsTheOriginalEntry: correction?.CorrectsTheOriginalEntry ?? true);
            })
            .ToList();

        // LOC-001 / COC-001. Current location and custody use EFFECTIVE values, so correcting a
        // location updates it rather than removing it from the projection.
        var latestLocation = EffectiveHistory.LatestOf<LocationEvent>(events);
        var latestCustody = EffectiveHistory.LatestOf<CustodyEvent>(events);

        return new ItemHistoryView(
            ItemId: item.Id,
            ItemNumber: item.ItemNumber,
            VoucherIdentifier: item.Voucher.DisplayIdentifier,
            CaseControlNumber: item.Voucher.Case?.CaseControlNumber ?? string.Empty,
            DescriptionForForm: item.DescriptionForForm,
            AccountabilityStatus: item.AccountabilityStatus,
            CurrentLocationPath: latestLocation?.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath)),
            CurrentCustodyHolder: latestCustody?.EffectiveValueOf(nameof(CustodyEvent.ReceivedBy)),
            History: rows,

            // AUD-008 — verified on every view so a broken chain surfaces where someone will see
            // it, not only when an integrity report is deliberately run.
            ChainVerification: EventHashChain.Verify(events),

            // AUD-016. The identifiers move with the corrections, so the row a projection points
            // at and the text a reader sees describe the same thing.
            CurrentLocationId: latestLocation?.EffectiveStorageLocationId,
            CurrentCustodyHolderPartyId: latestCustody?.EffectiveReceivedByPartyId);
    }

    /// <summary>
    /// Records a correction.
    ///
    /// AR 195-5 2-5b(5) — the erroneous entry is voided with one line "so it may still be read"
    /// and initialed. AR 195-5 1-7c(3) — the custodian immediately informs the supervisor and
    /// prepares an MFR outlining the error and the corrective action, filed with the DA Form 4137
    /// with a copy in the case file.
    ///
    /// The corrected event is preserved untouched; the correction is a new row pointing back at
    /// it. Nothing is overwritten (AUD-003, AUD-004, AUD-005, AUD-016).
    /// </summary>
    public async Task<OperationResult> RecordCorrectionAsync(
        RecordCorrectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var correctedEvent = await _db.ItemEvents
            .FirstOrDefaultAsync(e => e.Id == request.CorrectedEventId, ct);

        if (correctedEvent is null)
        {
            return OperationResult.Failure("The event to be corrected was not found.", "AUD-003");
        }

        var item = await _db.EvidenceItems
            .Include(i => i.Voucher)
            .FirstOrDefaultAsync(i => i.Id == correctedEvent.EvidenceItemId, ct);

        if (item?.Voucher is null)
        {
            return OperationResult.Failure("Item not found.", "ITEM-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.RecordCorrection, item.Voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            _audit.Record(
                AuditEventType.PermissionDenied,
                nameof(CorrectionEvent), request.CorrectedEventId.ToString(),
                reason: decision.Reason, succeeded: false);

            await _db.SaveChangesAsync(ct);
            return OperationResult.Failure(decision.Reason!, decision.RequirementId);
        }

        // AUD-015. A field may be corrected more than once, and a correction may itself be
        // corrected - the effective value is simply the most recent correction. The earlier
        // "one correction per event, ever" rule made a second mistake uncorrectable.
        if (!correctedEvent.IsCorrectableField(request.FieldName))
        {
            return OperationResult.Failure(
                $"'{request.FieldName}' is not a correctable field on a {correctedEvent.Kind} "
                + $"event. Correctable fields are: "
                + $"{string.Join(", ", correctedEvent.CorrectableFields.Keys)}.",
                "AUD-014");
        }

        // AUD-016. A field that names a ROW is corrected by naming the replacement row. The
        // display text is then read FROM THAT ROW, never from the request, so that the text and
        // the identifier the record carries cannot disagree.
        var referenceKind = correctedEvent.ReferenceKindOf(request.FieldName);

        string? correctedDisplayText = null;

        if (referenceKind != CorrectableFieldReference.None)
        {
            var resolution = await ResolveReplacementAsync(
                referenceKind, request, item.Voucher.EvidenceRoomId, ct);

            if (resolution.Failure is not null)
            {
                return resolution.Failure;
            }

            correctedDisplayText = resolution.DisplayText;
        }
        else if (request.CorrectedReferenceId is not null)
        {
            return OperationResult.Failure(
                $"'{request.FieldName}' is free text on a {correctedEvent.Kind} event and names "
                + "no row. A correction to it must not carry an identifier.",
                "AUD-016");
        }

        // AUD-017. Every correction already recorded against this event, so the factory can
        // derive what the field reads NOW - a second correction changes the first correction's
        // value, not the original entry. Loaded in full: a partial set would make that wrong.
        var existingCorrections = await _db.ItemEvents
            .OfType<CorrectionEvent>()
            .Where(c => c.CorrectsEventId == correctedEvent.Id)
            .ToListAsync(ct);

        var now = _clock.UtcNow;
        CorrectionEvent correction;

        try
        {
            // AUD-014. The original value is derived from the stored event, never taken from the
            // request - the caller cannot state what the record used to say.
            correction = referenceKind == CorrectableFieldReference.None
                ? CorrectionFactory.Create(
                    correctedEvent: correctedEvent,
                    existingCorrections: existingCorrections,
                    fieldName: request.FieldName,
                    correctedValue: request.CorrectedValue,
                    reason: request.Reason,
                    category: request.Category,
                    occurredAtLocal: now,
                    recordedAtUtc: now,
                    correctedByUserId: _currentUser.UserId,
                    mfrReference: request.MfrReference,
                    supervisorNotifiedUserId: request.SupervisorNotifiedUserId,
                    supervisorNotifiedAtUtc: request.SupervisorNotifiedAtUtc)
                : CorrectionFactory.CreateReferenceCorrection(
                    correctedEvent: correctedEvent,
                    existingCorrections: existingCorrections,
                    fieldName: request.FieldName,
                    correctedReferenceId: request.CorrectedReferenceId!.Value,
                    correctedDisplayText: correctedDisplayText!,
                    reason: request.Reason,
                    category: request.Category,
                    occurredAtLocal: now,
                    recordedAtUtc: now,
                    correctedByUserId: _currentUser.UserId,
                    mfrReference: request.MfrReference,
                    supervisorNotifiedUserId: request.SupervisorNotifiedUserId,
                    supervisorNotifiedAtUtc: request.SupervisorNotifiedAtUtc);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        await _events.AppendAsync(item, correction, ct);

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(CorrectionEvent),
            $"{item.Voucher.DisplayIdentifier}/{item.ItemNumber}#{correctedEvent.Id}",
            previousValue: correction.PreviousEffectiveValue,
            newValue: correction.CorrectedValue,
            reason: request.Reason);

        // One save. Nothing updates the corrected event, so there is no second pass to link a
        // supersession pointer (AUD-002).
        await _db.SaveChangesAsync(ct);

        var warnings = new List<string>();

        if (!correction.SatisfiesParagraph1_7c3)
        {
            // Surfaced rather than blocked: whether a given field-level correction rises to the
            // 1-7c(3) threshold is a matter of local policy, and an incomplete correction is
            // visible in the item history and to an inspector either way.
            // Only PostAcceptanceAccountabilityRecord corrections are subject to 1-7c(3). A
            // submitting agent correcting a draft under 2-3g, or a verifier fixing an OCR
            // transcription, is not a custodian finding an incorrect entry in the accountability
            // record, and demanding a custodian-error MFR for those would misstate the regulation.
            warnings.Add(
                "AR 195-5 para 1-7c(3): when a primary or alternate evidence custodian finds an "
                + "incorrect entry they will immediately inform the responsible CI supervisor and "
                + "prepare an MFR outlining the error and the corrective action taken, filed with "
                + "the proper DA Form 4137 with a copy in the case file. This correction records "
                + (correction.MfrReference is null ? "no MFR reference" : "an MFR reference")
                + " and "
                + (request.SupervisorNotifiedUserId is null
                    ? "no supervisor notification."
                    : "a supervisor notification."));
        }

        return OperationResult.Success([.. warnings]);
    }

    /// <summary>
    /// Resolves the replacement row a reference correction names, and reads its display text.
    ///
    /// The evidence-room check is the substantive one. AR 195-5 runs the document-number series
    /// (2-4c), inventories (3-2) and inspections (3-1) per evidence room; a location in another
    /// room is not a place this item can be. Invariant I-08 states it as a rule, and it is
    /// enforced identically here and when a location is first assigned - a correction must not be
    /// a way around a check the original action applied.
    /// </summary>
    private async Task<(OperationResult? Failure, string? DisplayText)> ResolveReplacementAsync(
        CorrectableFieldReference referenceKind,
        RecordCorrectionRequest request,
        int evidenceRoomId,
        CancellationToken ct)
    {
        if (request.CorrectedReferenceId is null)
        {
            return (OperationResult.Failure(
                $"'{request.FieldName}' names a {referenceKind}. Select the replacement rather "
                + "than typing a new value, so that the record keeps pointing at something real.",
                "AUD-016"), null);
        }

        var id = request.CorrectedReferenceId.Value;

        switch (referenceKind)
        {
            case CorrectableFieldReference.StorageLocation:
            {
                var location = await _db.StorageLocations
                    .AsNoTracking()
                    .Include(l => l.Parent)
                    .FirstOrDefaultAsync(l => l.Id == id, ct);

                if (location is null)
                {
                    return (OperationResult.Failure(
                        "Storage location not found.", "LOC-004"), null);
                }

                if (location.EvidenceRoomId != evidenceRoomId)
                {
                    // Reported the same way whether the location exists elsewhere or not, so a
                    // correction form cannot be used to probe another room's locations.
                    return (OperationResult.Failure(
                        "The selected storage location belongs to a different evidence room.",
                        "LOC-004"), null);
                }

                if (!location.IsActive)
                {
                    return (OperationResult.Failure(
                        "That storage location is no longer in use.", "LOC-004"), null);
                }

                return (null, location.FullPath);
            }

            case CorrectableFieldReference.CustodyParty:
            {
                // A custody party is a row created for the transfer it belongs to (COC-004), so a
                // correction names a party record that already exists - it is not created here.
                var party = await _db.CustodyParties
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == id, ct);

                return party is null
                    ? (OperationResult.Failure("Custody party not found.", "COC-004"), null)
                    : (null, party.DisplayName);
            }

            default:
                return (OperationResult.Failure(
                    $"Corrections to a {referenceKind} field are not supported.", "AUD-016"), null);
        }
    }

    private async Task<Dictionary<int, string>> ResolveUserNamesAsync(
        IReadOnlyCollection<ItemEvent> events, CancellationToken ct)
    {
        var ids = events.Select(e => e.RecordedByUserId).Distinct().ToList();

        return await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);
    }
}
