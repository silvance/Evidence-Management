using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Filing;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Filing;

public enum PhysicalDocumentAction
{
    FileOriginalInActiveFile = 1,
    ReleaseOriginalWithEvidence = 2,
    ReturnOriginalToActiveFile = 3,
    SendOriginalForDispositionApproval = 4,
    FileOriginalInactive = 5,
    TransferOriginalToGainingRoom = 6,
    FileCopyInactiveOriginalUnavailable = 7,
    ConfirmDestruction = 8,
    Note = 9
}

/// <summary>
/// One recorded step in the paper DA Form 4137's life. <paramref name="OccurredAt"/> is the
/// room-local instant the caller resolved; <paramref name="ContainerId"/> names the file the
/// action concerns (the active file, the suspense folder, the inactive file).
/// </summary>
public sealed record PhysicalDocumentActionRequest(
    int VoucherId,
    PhysicalDocumentAction Action,
    DateTimeOffset OccurredAt,
    int? ContainerId = null,
    string? Narrative = null,
    CopyRetentionReason CopyReason = CopyRetentionReason.None,
    string? GainingEvidenceRoom = null);

/// <summary>
/// A new paper file. An active file (2-4f(1)) states its canonical range - calendar year, first
/// and last sequence - and the service renders it in the room's numbering layout for the label.
/// </summary>
public sealed record CreateFileContainerRequest(
    int EvidenceRoomId,
    PhysicalFileKind Kind,
    ContainerForm Form,
    string Label,
    int? RangeCalendarYear = null,
    int? RangeFromSequence = null,
    int? RangeToSequence = null,
    int? DispositionYear = null,
    int? DispositionMonth = null,
    string? Notes = null);

public sealed record FileContainerRow(
    int Id, PhysicalFileKind Kind, ContainerForm Form, string Label,
    string? RangeFrom, string? RangeTo, int? RangeCalendarYear, int? RangeFromSequence, int? RangeToSequence,
    string? DispositionLabel, bool IsActive, int VouchersFiled);

public sealed record PhysicalDocumentEventRow(
    PhysicalDocumentEventKind Kind, OriginalDisposition ResultingOriginalDisposition, RetainedPaperStatus ResultingRetainedPaperStatus,
    string RecordedByName, DateTimeOffset OccurredAtUtc, string? ContainerLabel, string? Narrative);

/// <summary>The paper record for one voucher, as the voucher page shows it: what became of the original, and what this room holds now.</summary>
public sealed record PhysicalDocumentView(
    int VoucherId,
    OriginalDisposition OriginalDisposition,
    RetainedPaperStatus RetainedPaperStatus,
    string? CurrentContainerLabel,
    string? HomeActiveContainerLabel,
    bool HoldsCopyOnly,
    CopyRetentionReason CopyReason,
    bool SuspenseCopyFiledWithOriginal,
    DateTimeOffset? InactiveSinceUtc,
    DateTimeOffset? DestructionEligibleAtUtc,
    PaperRetentionStatus RetentionStatus,
    DateTimeOffset? DestructionConfirmedAtUtc,
    VoucherClosureBasis ClosureBasis,
    IReadOnlyList<PhysicalDocumentEventRow> Events,
    IReadOnlyList<FileContainerRow> RoomContainers,

    /// <summary>AR 195-5 2-7b copies: where the first copy is while copies are in use, how many copies are out, and whether the note was made.</summary>
    string? FirstCopyContainerLabel = null,
    int AdditionalCopiesOut = 0,
    bool CopiesMadeNoted = false);

public interface IPhysicalDocumentService
{
    Task<OperationResult<int>> CreateContainerAsync(CreateFileContainerRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FileContainerRow>> GetContainersAsync(int evidenceRoomId, CancellationToken ct = default);
    Task<PhysicalDocumentView?> GetForVoucherAsync(int voucherId, CancellationToken ct = default);
    Task<OperationResult> RecordAsync(PhysicalDocumentActionRequest request, CancellationToken ct = default);
}

/// <summary>
/// The custodian's record of the PHYSICAL DA Form 4137: filing, release with the evidence, return,
/// disposition approval, inactive filing, permanent transfer, copy-only cases, and confirmed
/// destruction (AR 195-5 2-4d, 2-4f, 2-4g, 2-4h, 2-7g). Maintaining these files is the primary
/// custodian's duty (1-4h(2)), so every write needs an active custodian appointment.
///
/// Nothing here reads, writes or references a scan. A scan is a companion copy with its own
/// provenance; this is where the paper is.
/// </summary>
public sealed class PhysicalDocumentService : IPhysicalDocumentService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;

    public PhysicalDocumentService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        ICurrentUser currentUser,
        IAuditRecorder audit,
        IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _clock = clock;
    }

    public async Task<OperationResult<int>> CreateContainerAsync(CreateFileContainerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ManagePhysicalFiles, request.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            return await DenyAsync<int>(decision, nameof(PhysicalFileContainer), null, ct);
        }

        PhysicalFileContainer container;
        try
        {
            // The label range is rendered in the room's own numbering layout (VCH-023); the
            // canonical range is what filing is checked against.
            string? from = null, to = null;
            if (request.Kind == PhysicalFileKind.Active4137File && request.RangeCalendarYear is int year
                && request.RangeFromSequence is int first && request.RangeToSequence is int last && first >= 1 && last >= first)
            {
                var policy = await ResolveNumberingPolicyAsync(request.EvidenceRoomId, _clock.UtcNow, ct);
                from = policy.Format(first, year);
                to = policy.Format(last, year);
            }

            container = new PhysicalFileContainer(
                request.EvidenceRoomId, request.Kind, request.Form, request.Label,
                request.RangeCalendarYear, request.RangeFromSequence, request.RangeToSequence, from, to,
                request.DispositionYear, request.DispositionMonth, request.Notes);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        _db.PhysicalFileContainers.Add(container);
        _audit.Record(AuditEventType.ConfigurationChanged, nameof(PhysicalFileContainer), container.Label,
            newValue: $"{container.Kind} {container.Form}");
        await _db.SaveChangesAsync(ct);
        return OperationResult<int>.Success(container.Id);
    }

    public async Task<IReadOnlyList<FileContainerRow>> GetContainersAsync(int evidenceRoomId, CancellationToken ct = default)
    {
        if (!(await _authorization.AuthorizeAsync(EmcPermissions.ViewVoucher, evidenceRoomId, ct)).IsAllowed)
        {
            return [];
        }

        var containers = await _db.PhysicalFileContainers.AsNoTracking()
            .Where(c => c.EvidenceRoomId == evidenceRoomId)
            .OrderBy(c => c.Kind).ThenBy(c => c.Label)
            .ToListAsync(ct);

        return containers.Select(c => new FileContainerRow(
            c.Id, c.Kind, c.Form, c.Label, c.DocumentNumberRangeFrom, c.DocumentNumberRangeTo,
            c.RangeCalendarYear, c.RangeFromSequence, c.RangeToSequence,
            c.DispositionLabel, c.IsActive, c.FiledVoucherCount)).ToList();
    }

    public async Task<PhysicalDocumentView?> GetForVoucherAsync(int voucherId, CancellationToken ct = default)
    {
        var roomId = await _db.EvidenceVouchers.AsNoTracking()
            .Where(v => v.Id == voucherId).Select(v => (int?)v.EvidenceRoomId).FirstOrDefaultAsync(ct);

        if (roomId is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewVoucher, roomId, ct)).IsAllowed)
        {
            return null;
        }

        var document = await _db.PhysicalVoucherDocuments.AsNoTracking()
            .Include(d => d.Events)
            .FirstOrDefaultAsync(d => d.VoucherId == voucherId, ct);

        var containers = await GetContainersAsync(roomId.Value, ct);
        var labels = containers.ToDictionary(c => c.Id, c => c.Label);

        var voucher = await _db.EvidenceVouchers.AsNoTracking().Include(v => v.Items).FirstAsync(v => v.Id == voucherId, ct);
        if (document is null)
        {
            return new PhysicalDocumentView(
                voucherId, OriginalDisposition.NotYetFiled, RetainedPaperStatus.None, null, null, false,
                CopyRetentionReason.None, false, null, null, PaperRetentionStatus.Retain, null, voucher.ClosureBasis, [], containers);
        }

        var userIds = document.Events.Select(e => e.RecordedByUserId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);

        return new PhysicalDocumentView(
            voucherId,
            document.OriginalDisposition,
            document.RetainedPaperStatus,
            Label(document.CurrentContainerId),
            Label(document.HomeActiveContainerId),
            document.HoldsCopyOnly,
            document.CopyReason,
            document.SuspenseCopyFiledWithOriginal,
            document.InactiveSinceUtc,
            document.DestructionEligibleAtUtc,
            document.RetentionStatusAt(_clock.UtcNow),
            document.DestructionConfirmedAtUtc,
            voucher.ClosureBasis,
            document.Events.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id)
                .Select(e => new PhysicalDocumentEventRow(
                    e.Kind, e.ResultingOriginalDisposition, e.ResultingRetainedPaperStatus, names.GetValueOrDefault(e.RecordedByUserId, "(unknown user)"),
                    e.OccurredAtUtc, Label(e.ContainerId), e.Narrative))
                .ToList(),
            containers,
            Label(document.FirstCopyContainerId),
            document.AdditionalCopiesOut,
            document.CopiesMadeNoted);

        string? Label(int? id) => id is null ? null : labels.GetValueOrDefault(id.Value, $"(container {id})");
    }

    public async Task<OperationResult> RecordAsync(PhysicalDocumentActionRequest request, CancellationToken ct = default)
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

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.ManagePhysicalFiles, voucher.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            var denied = await DenyAsync<bool>(decision, nameof(PhysicalVoucherDocument), voucher.DisplayIdentifier, ct);
            return OperationResult.Failure(denied.Error!, denied.RequirementId);
        }

        // The paper original exists to be filed once the custodian has received the evidence and
        // numbered the form (2-4c, 2-4d). Before that there is nothing for this room to file.
        if (!voucher.HasOfficialDocumentNumber && request.Action != PhysicalDocumentAction.Note)
        {
            return OperationResult.Failure(
                "AR 195-5 para 2-4d: the evidence custodian retains the original DA Form 4137 after the "
                + "document number is assigned. This voucher has not been received and numbered.",
                "FIL-004");
        }

        var document = await _db.PhysicalVoucherDocuments
            .Include(d => d.Events)
            .FirstOrDefaultAsync(d => d.VoucherId == voucher.Id, ct);

        var isNew = document is null;
        document ??= new PhysicalVoucherDocument(voucher.Id, voucher.EvidenceRoomId);

        PhysicalFileContainer? container = null;
        if (request.ContainerId is int containerId)
        {
            container = await _db.PhysicalFileContainers.FirstOrDefaultAsync(c => c.Id == containerId, ct);
            if (container is null || container.EvidenceRoomId != voucher.EvidenceRoomId)
            {
                // Reported the same way whether it exists in another room or not at all.
                return OperationResult.Failure("File container not found in this evidence room.", "FIL-001");
            }
        }

        var userId = _currentUser.UserId;
        var at = request.OccurredAt;
        var before = $"{document.OriginalDisposition}/{document.RetainedPaperStatus}";
        var current = document.CurrentContainerId is int currentId
            ? await _db.PhysicalFileContainers.FirstOrDefaultAsync(c => c.Id == currentId, ct)
            : null;
        var assignment = voucher.CurrentDocumentNumberAssignment;

        try
        {
            switch (request.Action)
            {
                case PhysicalDocumentAction.FileOriginalInActiveFile:
                    document.FileOriginalInActiveFile(Required(container), assignment!.Sequence, assignment.CalendarYear, userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.ReleaseOriginalWithEvidence:
                    document.ReleaseOriginalWithEvidence(RequiredCurrent(current), Required(container), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.ReturnOriginalToActiveFile:
                    document.ReturnOriginalToActiveFile(Required(container), RequiredCurrent(current), assignment!.Sequence, assignment.CalendarYear, userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.SendOriginalForDispositionApproval:
                    document.SendOriginalForDispositionApproval(RequiredCurrent(current), Required(container), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.FileOriginalInactive:
                    document.FileOriginalInactive(Required(container), current, voucher.ClosureBasis, userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.TransferOriginalToGainingRoom:
                    document.TransferOriginalToGainingRoom(Required(container), current, voucher.ClosureBasis, request.GainingEvidenceRoom ?? string.Empty, userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.FileCopyInactiveOriginalUnavailable:
                    document.FileCopyInactiveBecauseOriginalUnavailable(Required(container), current, request.CopyReason, request.Narrative ?? string.Empty, userId, at);
                    break;

                case PhysicalDocumentAction.ConfirmDestruction:
                    document.ConfirmDestruction(current, userId, at, request.Narrative ?? string.Empty);
                    break;

                case PhysicalDocumentAction.Note:
                    document.AddNote(userId, at, request.Narrative ?? string.Empty);
                    break;

                default:
                    return OperationResult.Failure("Unknown physical-document action.", "FIL-004");
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        // Attached only once the action succeeded, so a refused first action leaves no
        // half-built record in the unit of work.
        if (isNew)
        {
            _db.PhysicalVoucherDocuments.Add(document);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(PhysicalVoucherDocument), voucher.DisplayIdentifier,
            previousValue: before, newValue: $"{document.OriginalDisposition}/{document.RetainedPaperStatus}",
            reason: $"{request.Action}: {request.Narrative}".Trim());

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // FIL-002. Another filing changed the container (its count and stamp) between our
            // read and our write - the 50th-voucher race. Nothing was written; the caller retries
            // against the container's current state.
            return OperationResult.Failure(
                "The file container was changed by another filing at the same moment. Nothing was recorded; check the container's current contents and try again.",
                "FIL-002");
        }

        return OperationResult.Success();

        static PhysicalFileContainer Required(PhysicalFileContainer? c)
            => c ?? throw new DomainRuleViolationException("FIL-001", "This action names a file container.");

        static PhysicalFileContainer RequiredCurrent(PhysicalFileContainer? c)
            => c ?? throw new DomainRuleViolationException("FIL-001", "The paper record names no current container for this action.");
    }

    /// <summary>The room's numbering policy in effect now; the regulation's layout when none is recorded.</summary>
    private async Task<EvidenceRoomNumberingPolicy> ResolveNumberingPolicyAsync(int evidenceRoomId, DateTimeOffset at, CancellationToken ct)
    {
        var policies = await _db.EvidenceRoomNumberingPolicies.AsNoTracking().Where(p => p.EvidenceRoomId == evidenceRoomId).ToListAsync(ct);
        return policies.Where(p => p.IsEffectiveAt(at)).OrderByDescending(p => p.EffectiveFrom).FirstOrDefault()
               ?? EvidenceRoomNumberingPolicy.Regulatory(evidenceRoomId, DateTimeOffset.MinValue);
    }

    private async Task<OperationResult<T>> DenyAsync<T>(AuthorizationDecision decision, string recordType, string? recordId, CancellationToken ct)
    {
        _audit.Record(AuditEventType.PermissionDenied, recordType, recordId, reason: decision.Reason, succeeded: false);
        await _db.SaveChangesAsync(ct);
        return OperationResult<T>.Failure(decision.Reason!, decision.RequirementId);
    }
}
