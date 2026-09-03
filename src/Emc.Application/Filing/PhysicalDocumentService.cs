using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Filing;
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

public sealed record CreateFileContainerRequest(
    int EvidenceRoomId,
    PhysicalFileKind Kind,
    ContainerForm Form,
    string Label,
    string? DocumentNumberRangeFrom = null,
    string? DocumentNumberRangeTo = null,
    int? DispositionYear = null,
    int? DispositionMonth = null,
    string? Notes = null);

public sealed record FileContainerRow(
    int Id, PhysicalFileKind Kind, ContainerForm Form, string Label,
    string? RangeFrom, string? RangeTo, string? DispositionLabel, bool IsActive, int VouchersFiled);

public sealed record PhysicalDocumentEventRow(
    PhysicalDocumentEventKind Kind, PhysicalOriginalStatus ResultingStatus, string RecordedByName,
    DateTimeOffset OccurredAtUtc, string? ContainerLabel, string? Narrative);

/// <summary>The paper record for one voucher, as the voucher page shows it.</summary>
public sealed record PhysicalDocumentView(
    int VoucherId,
    PhysicalOriginalStatus OriginalStatus,
    string? OriginalContainerLabel,
    string? SuspenseCopyContainerLabel,
    string? InactiveContainerLabel,
    bool HoldsCopyOnly,
    CopyRetentionReason CopyReason,
    DateTimeOffset? InactiveSinceUtc,
    DateTimeOffset? DestructionEligibleAtUtc,
    PaperRetentionStatus RetentionStatus,
    DateTimeOffset? DestructionConfirmedAtUtc,
    IReadOnlyList<PhysicalDocumentEventRow> Events,
    IReadOnlyList<FileContainerRow> RoomContainers);

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
            container = new PhysicalFileContainer(
                request.EvidenceRoomId, request.Kind, request.Form, request.Label,
                request.DocumentNumberRangeFrom, request.DocumentNumberRangeTo,
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

        var counts = await FiledCountsAsync(containers.Select(c => c.Id).ToList(), ct);

        return containers.Select(c => new FileContainerRow(
            c.Id, c.Kind, c.Form, c.Label, c.DocumentNumberRangeFrom, c.DocumentNumberRangeTo,
            c.DispositionLabel, c.IsActive, counts.GetValueOrDefault(c.Id))).ToList();
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

        if (document is null)
        {
            return new PhysicalDocumentView(
                voucherId, PhysicalOriginalStatus.NotYetFiled, null, null, null, false,
                CopyRetentionReason.None, null, null, PaperRetentionStatus.Retain, null, [], containers);
        }

        var userIds = document.Events.Select(e => e.RecordedByUserId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);

        return new PhysicalDocumentView(
            voucherId,
            document.OriginalStatus,
            Label(document.OriginalContainerId),
            Label(document.SuspenseCopyContainerId),
            Label(document.InactiveContainerId),
            document.HoldsCopyOnly,
            document.CopyReason,
            document.InactiveSinceUtc,
            document.DestructionEligibleAtUtc,
            document.RetentionStatusAt(_clock.UtcNow),
            document.DestructionConfirmedAtUtc,
            document.Events.OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id)
                .Select(e => new PhysicalDocumentEventRow(
                    e.Kind, e.ResultingOriginalStatus, names.GetValueOrDefault(e.RecordedByUserId, "(unknown user)"),
                    e.OccurredAtUtc, Label(e.ContainerId), e.Narrative))
                .ToList(),
            containers);

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

        if (document is null)
        {
            document = new PhysicalVoucherDocument(voucher.Id, voucher.EvidenceRoomId);
            _db.PhysicalVoucherDocuments.Add(document);
        }

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
        var before = document.OriginalStatus;

        try
        {
            switch (request.Action)
            {
                case PhysicalDocumentAction.FileOriginalInActiveFile:
                    document.FileOriginalInActiveFile(Required(container), await FiledCountAsync(Required(container).Id, ct), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.ReleaseOriginalWithEvidence:
                    document.ReleaseOriginalWithEvidence(Required(container), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.ReturnOriginalToActiveFile:
                    document.ReturnOriginalToActiveFile(Required(container), await FiledCountAsync(Required(container).Id, ct), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.SendOriginalForDispositionApproval:
                    document.SendOriginalForDispositionApproval(Required(container), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.FileOriginalInactive:
                    // AR 195-5 2-4h: "After ALL items of evidence listed on a DA Form 4137 have
                    // been properly disposed". The voucher's derived status is the test.
                    if (voucher.DerivedStatus != VoucherDerivedStatus.Inactive)
                    {
                        return OperationResult.Failure(
                            "AR 195-5 para 2-4h: the original DA Form 4137 moves to the inactive file "
                            + "after ALL items listed on it have been properly disposed. This voucher "
                            + $"is {voucher.DerivedStatus}.", "FIL-006");
                    }

                    document.FileOriginalInactive(Required(container), userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.TransferOriginalToGainingRoom:
                    if (voucher.CurrentFormLines.Any(i => i.AccountabilityStatus != AccountabilityStatus.PermanentlyTransferred))
                    {
                        return OperationResult.Failure(
                            "AR 195-5 para 2-7g: the original and duplicate DA Form 4137 accompany the "
                            + "evidence on permanent transfer. Record the items' permanent transfer "
                            + "first; this voucher still has items accounted for in this room.", "FIL-007");
                    }

                    document.TransferOriginalToGainingRoom(Required(container), request.GainingEvidenceRoom ?? string.Empty, userId, at, request.Narrative);
                    break;

                case PhysicalDocumentAction.FileCopyInactiveOriginalUnavailable:
                    document.FileCopyInactiveBecauseOriginalUnavailable(Required(container), request.CopyReason, request.Narrative ?? string.Empty, userId, at);
                    break;

                case PhysicalDocumentAction.ConfirmDestruction:
                    document.ConfirmDestruction(userId, at, request.Narrative ?? string.Empty);
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

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(PhysicalVoucherDocument), voucher.DisplayIdentifier,
            previousValue: before.ToString(), newValue: document.OriginalStatus.ToString(),
            reason: $"{request.Action}: {request.Narrative}".Trim());

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();

        static PhysicalFileContainer Required(PhysicalFileContainer? c)
            => c ?? throw new DomainRuleViolationException("FIL-001", "This action names a file container.");
    }

    private async Task<int> FiledCountAsync(int containerId, CancellationToken ct)
        => await _db.PhysicalVoucherDocuments.AsNoTracking()
            .CountAsync(d => d.OriginalContainerId == containerId, ct);

    private async Task<Dictionary<int, int>> FiledCountsAsync(List<int> containerIds, CancellationToken ct)
        => await _db.PhysicalVoucherDocuments.AsNoTracking()
            .Where(d => d.OriginalContainerId != null && containerIds.Contains(d.OriginalContainerId.Value)
                        || d.SuspenseCopyContainerId != null && containerIds.Contains(d.SuspenseCopyContainerId.Value)
                        || d.InactiveContainerId != null && containerIds.Contains(d.InactiveContainerId.Value))
            .Select(d => new { d.OriginalContainerId, d.SuspenseCopyContainerId, d.InactiveContainerId })
            .ToListAsync(ct)
            .ContinueWith(t => t.Result
                .SelectMany(d => new[] { d.OriginalContainerId, d.SuspenseCopyContainerId, d.InactiveContainerId })
                .Where(id => id is not null)
                .GroupBy(id => id!.Value)
                .ToDictionary(g => g.Key, g => g.Count()), ct);

    private async Task<OperationResult<T>> DenyAsync<T>(AuthorizationDecision decision, string recordType, string? recordId, CancellationToken ct)
    {
        _audit.Record(AuditEventType.PermissionDenied, recordType, recordId, reason: decision.Reason, succeeded: false);
        await _db.SaveChangesAsync(ct);
        return OperationResult<T>.Failure(decision.Reason!, decision.RequirementId);
    }
}
