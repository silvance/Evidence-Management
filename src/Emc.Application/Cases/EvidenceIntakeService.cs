using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Items;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Configuration;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Cases;

public sealed record RecordDocumentNumberRequest(
    int VoucherId,
    string DocumentNumber,
    bool AttestedAssignedInAuthoritativeLedger,
    DateTimeOffset ReceivedAtLocal,
    string? SupersessionReason = null);

public sealed record AssignLocationRequest(
    int ItemId,
    int StorageLocationId,
    DateTimeOffset OccurredAtLocal,
    string? Reason,
    string? Notes);

public interface IEvidenceIntakeService
{
    Task<OperationResult> RecordOfficialDocumentNumberAsync(
        RecordDocumentNumberRequest request, CancellationToken ct = default);

    Task<OperationResult> AssignStorageLocationAsync(
        AssignLocationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Evidence-room intake: recording the official document number and assigning storage locations.
///
/// This is the most regulatorily sensitive service in the first slice, because AR 195-5 2-4c
/// makes assignment of the document number the evidence custodian's act, performed by order of
/// precedence from the evidence ledger — and para 2-5a requires that ledger to be a bound book
/// unless the system has been approved under 2-5c (for CI organizations, by Army G-2X).
///
/// So EMC TRANSCRIBES a number a custodian assigned on paper. It does not generate one
/// (EMC-002, VCH-006).
/// </summary>
public sealed class EvidenceIntakeService : IEvidenceIntakeService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IItemEventRecorder _events;
    private readonly IClock _clock;

    public EvidenceIntakeService(
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

    public async Task<OperationResult> RecordOfficialDocumentNumberAsync(
        RecordDocumentNumberRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var voucher = await _db.EvidenceVouchers
            .Include(v => v.Items)
            .Include(v => v.DocumentNumberAssignments)
            .FirstOrDefaultAsync(v => v.Id == request.VoucherId, ct);

        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "VCH-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.RecordOfficialDocumentNumber, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return await DenyAsync(decision, nameof(EvidenceVoucher), voucher.DisplayIdentifier, ct);
        }

        // EMC-002. Belt and braces: even if a caller reached this method, companion mode refuses
        // any attempt to have the SYSTEM originate the number. The custodian must attest that the
        // number came from the authoritative ledger (AR 195-5 2-4c, 2-5a, 2-5c).
        var configuration = await _db.SystemConfigurations.AsNoTracking().FirstOrDefaultAsync(ct);
        if (configuration is { NumberingMode: NumberingMode.ManualTranscription }
            && !request.AttestedAssignedInAuthoritativeLedger)
        {
            return OperationResult.Failure(
                "AR 195-5 para 2-4c: the evidence custodian assigns the document number by order "
                + "of precedence from the evidence ledger. This application operates as a "
                + "companion under para 2-5c and cannot originate the number. Confirm that the "
                + "number was assigned in the authoritative evidence ledger before recording it "
                + "here.",
                "EMC-002");
        }

        if (!EvidenceDocumentNumber.TryParse(request.DocumentNumber, out var documentNumber))
        {
            return OperationResult.Failure(
                "AR 195-5 para 2-4c: the evidence document number consists of two groups of "
                + "digits separated by a hyphen - a three-digit sequence beginning at 001 for the "
                + "first DA Form 4137 received for the calendar year, then the two-digit calendar "
                + $"year (for example 037-26). Received '{request.DocumentNumber}'.",
                "VCH-004");
        }

        var previous = voucher.CurrentDocumentNumberAssignment;

        // Invariant I-04. AR 195-5 2-4c scopes the series to the calendar year and 2-7g shows it
        // belongs to the evidence room, so uniqueness is per (room, year, sequence) — NEVER
        // global. A database unique index enforces the same thing; this check exists to produce a
        // usable message rather than a constraint violation.
        var collision = await _db.DocumentNumberAssignments
            .AsNoTracking()
            .AnyAsync(a => a.EvidenceRoomId == voucher.EvidenceRoomId
                           && a.CalendarYear == documentNumber.CalendarYear
                           && a.Sequence == documentNumber.Sequence
                           && a.SupersededByAssignmentId == null
                           && a.VoucherId != voucher.Id, ct);

        if (collision)
        {
            return OperationResult.Failure(
                $"Document number {documentNumber} is already recorded against another voucher in "
                + "this evidence room for this calendar year. Verify the entry against the "
                + "evidence ledger (AR 195-5 para 2-4c).",
                "VCH-005");
        }

        var now = _clock.UtcNow;
        OfficialDocumentNumberAssignment assignment;

        try
        {
            assignment = voucher.RecordOfficialDocumentNumber(
                documentNumber: documentNumber,
                enteredByUserId: _currentUser.UserId,
                enteredAtUtc: now,
                attestedAssignedInAuthoritativeLedger: request.AttestedAssignedInAuthoritativeLedger,
                supersessionReason: request.SupersessionReason);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        // Each item records the assignment in its own history, and moves into the evidence room.
        // AR 195-5 2-4c ties the document number to receipt of the evidence by the custodian, so
        // acceptance and numbering happen together (invariant I-12).
        foreach (var item in voucher.Items.OrderBy(i => i.ItemNumber))
        {
            await _events.AppendAsync(
                item,
                new DocumentNumberEvent(
                    documentNumber: assignment.DocumentNumber,
                    previousDocumentNumber: previous?.DocumentNumber,
                    attestedAssignedInAuthoritativeLedger: true,
                    occurredAtLocal: request.ReceivedAtLocal,
                    recordedAtUtc: now,
                    recordedByUserId: _currentUser.UserId),
                ct);

            if (item.AccountabilityStatus == AccountabilityStatus.AwaitingCustodian)
            {
                var from = item.AccountabilityStatus;
                item.TransitionTo(AccountabilityStatus.InEvidenceRoom);

                await _events.AppendAsync(
                    item,
                    new StatusEvent(
                        fromStatus: from,
                        toStatus: AccountabilityStatus.InEvidenceRoom,
                        reason: $"Received by the evidence custodian and assigned evidence document "
                                + $"number {assignment.DocumentNumber} (AR 195-5 2-4c).",
                        occurredAtLocal: request.ReceivedAtLocal,
                        recordedAtUtc: now,
                        recordedByUserId: _currentUser.UserId),
                    ct);
            }
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(OfficialDocumentNumberAssignment), assignment.DocumentNumber,
            previousValue: previous?.DocumentNumber ?? voucher.TemporaryIdentifier,
            newValue: assignment.DocumentNumber,
            reason: request.SupersessionReason);

        await _db.SaveChangesAsync(ct);

        var warnings = new List<string>();

        // VCH-009. EMC cannot know the ledger's true state, so a gap is a warning and never a
        // block: the custodian holds the authoritative record, not the application.
        var gapWarning = await DetectSequenceGapAsync(
            voucher.EvidenceRoomId, documentNumber, ct);

        if (gapWarning is not null)
        {
            warnings.Add(gapWarning);
        }

        return OperationResult.Success([.. warnings]);
    }

    /// <summary>
    /// Records an item's storage location within the evidence room.
    ///
    /// AR 195-5 2-4e requires only the CURRENT location, recorded in pencil on the DA Form 4137,
    /// kept current "by erasing the previous entry and noting the new location". EMC retains the
    /// full history anyway as a design and integrity control (LOC-002) — and must never claim the
    /// regulation requires that history (LOC-003).
    /// </summary>
    public async Task<OperationResult> AssignStorageLocationAsync(
        AssignLocationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = await _db.EvidenceItems
            .Include(i => i.Voucher)
            .FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);

        if (item?.Voucher is null)
        {
            return OperationResult.Failure("Item not found.", "ITEM-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.AssignStorageLocation, item.Voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return await DenyAsync(decision, nameof(EvidenceItem), item.Id.ToString(), ct);
        }

        var location = await _db.StorageLocations
            .Include(l => l.Parent)
            .FirstOrDefaultAsync(l => l.Id == request.StorageLocationId, ct);

        if (location is null)
        {
            return OperationResult.Failure("Storage location not found.", "LOC-004");
        }

        // Invariant I-08 — a location must resolve within the item's own evidence room.
        if (location.EvidenceRoomId != item.Voucher.EvidenceRoomId)
        {
            return OperationResult.Failure(
                "The selected storage location belongs to a different evidence room.", "LOC-004");
        }

        if (!location.IsActive)
        {
            return OperationResult.Failure(
                "That storage location is no longer in use.", "LOC-004");
        }

        // Invariant I-12. AR 195-5 2-4e places the location in the DA Form 4137's location block,
        // which presupposes the evidence has been received into the evidence room under 2-4c.
        if (item.AccountabilityStatus is AccountabilityStatus.Draft
            or AccountabilityStatus.Acquired
            or AccountabilityStatus.AwaitingCustodian)
        {
            return OperationResult.Failure(
                "AR 195-5 para 2-4c: the evidence must be received by the evidence custodian and "
                + "assigned a document number before an evidence-room location is recorded.",
                "ITEM-001");
        }

        var previous = item.CurrentLocationEvent?.StorageLocationPath;
        var now = _clock.UtcNow;

        await _events.AppendAsync(
            item,
            new LocationEvent(
                storageLocationId: location.Id,

                // Denormalized: an append-only history must stay readable exactly as recorded
                // even if the location is later renamed or retired.
                storageLocationPath: location.FullPath,
                occurredAtLocal: request.OccurredAtLocal,
                recordedAtUtc: now,
                recordedByUserId: _currentUser.UserId,
                reason: request.Reason,
                notes: request.Notes),
            ct);

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(LocationEvent),
            $"{item.Voucher.DisplayIdentifier}/{item.ItemNumber}",
            previousValue: previous,
            newValue: location.FullPath,
            reason: request.Reason);

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    /// <summary>
    /// VCH-009 — a non-blocking advisory when the preceding sequence number is not recorded for
    /// this evidence room and calendar year. AR 195-5 2-4c assigns numbers in order of precedence
    /// from the ledger, so a gap here usually means a voucher has not yet been entered into EMC —
    /// not that the ledger is wrong. EMC warns and never blocks.
    /// </summary>
    private async Task<string?> DetectSequenceGapAsync(
        int evidenceRoomId, EvidenceDocumentNumber documentNumber, CancellationToken ct)
    {
        if (documentNumber.Sequence <= 1)
        {
            return null;
        }

        var previousExists = await _db.DocumentNumberAssignments
            .AsNoTracking()
            .AnyAsync(a => a.EvidenceRoomId == evidenceRoomId
                           && a.CalendarYear == documentNumber.CalendarYear
                           && a.Sequence == documentNumber.Sequence - 1, ct);

        if (previousExists)
        {
            return null;
        }

        var expected = $"{documentNumber.Sequence - 1:D3}-{documentNumber.TwoDigitYear:D2}";

        return $"Document number {expected} is not recorded in this companion for this evidence "
               + "room. AR 195-5 para 2-4c assigns document numbers by order of precedence from "
               + "the evidence ledger, so this usually means the preceding voucher has not been "
               + "entered here yet. The evidence ledger remains the authoritative record; this is "
               + "an advisory only.";
    }

    private async Task<OperationResult> DenyAsync(
        AuthorizationDecision decision, string recordType, string? recordId, CancellationToken ct)
    {
        _audit.Record(
            AuditEventType.PermissionDenied,
            recordType, recordId, reason: decision.Reason, succeeded: false);

        await _db.SaveChangesAsync(ct);
        return OperationResult.Failure(decision.Reason!, decision.RequirementId);
    }
}
