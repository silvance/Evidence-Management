using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Reads;

public sealed record CaseListRow(
    int Id, string CaseControlNumber, string Title, bool IsClosed, int VoucherCount, string EvidenceRoomName);

public sealed record CaseDetailView(
    int Id,
    string CaseControlNumber,
    string Title,
    string? Synopsis,
    int EvidenceRoomId,
    bool IsClosed,
    IReadOnlyList<VoucherListRow> Vouchers);

public sealed record VoucherListRow(
    int Id,
    string DisplayIdentifier,
    bool HasOfficialDocumentNumber,
    VoucherDerivedStatus DerivedStatus,
    int ItemCount,
    DateTimeOffset AcquiredAtLocal);

public sealed record EvidenceRoomOption(int Id, string Name);

public sealed record VoucherDetailView(
    int Id,
    int CaseId,
    string CaseControlNumber,
    string? RequestingOfficeCaseNumber,
    int EvidenceRoomId,
    string DisplayIdentifier,
    string TemporaryIdentifier,
    bool HasOfficialDocumentNumber,
    bool IsSubmitted,
    bool AllowsItemEditing,
    VoucherDerivedStatus DerivedStatus,
    string ReceivingActivity,
    string ReceivingActivityLocation,
    string ReceivedFrom,
    DateTimeOffset AcquiredAtLocal,
    IReadOnlyList<ItemListRow> Items,
    IReadOnlyList<DocumentNumberRow> DocumentNumbers,

    /// <summary>AR 195-5 2-3g - where the form stands in the custodian's review, and how it got there.</summary>
    VoucherReviewStage ReviewStage = VoucherReviewStage.Draft,
    IReadOnlyList<VoucherReviewActionRow>? ReviewActions = null,
    int? SubmittedByUserId = null,

    /// <summary>
    /// How this room writes document numbers (VCH-023): a description for the form, an example,
    /// and whether the layout is the regulation's or a local one. The page shows this instead of
    /// hard-coding the regulation's layout.
    /// </summary>
    string? DocumentNumberFormatDescription = null,
    string? DocumentNumberExample = null,
    bool DocumentNumberLayoutIsRegulatory = true,
    bool DocumentNumberLayoutAwaitsValidation = false);

public sealed record VoucherReviewActionRow(
    VoucherReviewActionKind Kind,
    VoucherReviewStage ResultingStage,
    string ActorName,
    DateTimeOffset OccurredAtUtc,
    string? Narrative,
    bool? PaperFormCorrectedAndInitialedAttested);

public sealed record ItemListRow(
    int Id,
    int ItemNumber,
    string DescriptionForForm,
    string? Quantity,
    string? SerialNumber,
    string? UniqueDeviceIdentifier,
    bool IsPossibleBiohazard,
    bool IsSealed,
    AccountabilityStatus AccountabilityStatus,
    bool IsLastItem);

public sealed record DocumentNumberRow(
    string DocumentNumber, DateTimeOffset EnteredAtUtc, bool IsCurrent, string? SupersessionReason);

/// <summary>
/// Authorized read access to case and voucher data.
///
/// Every method authorizes BEFORE querying, and scopes the query to evidence rooms the user
/// actually holds a grant in. This exists because authentication is not authorization: the
/// ASP.NET fallback policy proves only that a Windows principal authenticated, and a domain
/// account with no EMC role must be able to read nothing at all (IAM-017).
///
/// A record the caller may not read is reported as ABSENT rather than forbidden, so that
/// enumerating integer identifiers cannot confirm which records exist (IAM-018).
///
/// Query logic lives here rather than in Razor page models so that no page can accidentally
/// reach past the authorization check to the DbContext.
/// </summary>
public interface IEvidenceReadService
{
    Task<IReadOnlyList<EvidenceRoomOption>> GetAccessibleEvidenceRoomsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CaseListRow>> GetAccessibleCasesAsync(CancellationToken ct = default);

    Task<CaseDetailView?> GetCaseAsync(int caseId, CancellationToken ct = default);

    Task<VoucherDetailView?> GetVoucherAsync(int voucherId, CancellationToken ct = default);

    /// <summary>The evidence room an item belongs to, or null when the caller may not read it.</summary>
    Task<int?> GetReadableItemEvidenceRoomIdAsync(int itemId, CancellationToken ct = default);
}

public sealed class EvidenceReadService : IEvidenceReadService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;

    public EvidenceReadService(
        IEmcDbContext db, IEvidenceAuthorizationService authorization, ICurrentUser currentUser)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<EvidenceRoomOption>> GetAccessibleEvidenceRoomsAsync(
        CancellationToken ct = default)
    {
        var accessible = _currentUser.AccessibleEvidenceRoomIds();
        if (accessible.Count == 0)
        {
            return [];
        }

        return await _db.EvidenceRooms
            .AsNoTracking()
            .Where(r => r.IsActive && accessible.Contains(r.Id))
            .OrderBy(r => r.Name)
            .Select(r => new EvidenceRoomOption(r.Id, r.Name))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CaseListRow>> GetAccessibleCasesAsync(CancellationToken ct = default)
    {
        // Rooms the user can actually read cases in - checked room by room, so holding a role in
        // one room never exposes another (IAM-016).
        var readableRooms = await GetReadableRoomIdsAsync(EmcPermissions.ViewCase, ct);
        if (readableRooms.Count == 0)
        {
            return [];
        }

        return await _db.Cases
            .AsNoTracking()
            .Where(c => readableRooms.Contains(c.EvidenceRoomId))
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new CaseListRow(
                c.Id,
                c.CaseControlNumber,
                c.Title,
                c.IsClosed,
                c.Vouchers.Count,
                _db.EvidenceRooms.Where(r => r.Id == c.EvidenceRoomId).Select(r => r.Name).First()))
            .ToListAsync(ct);
    }

    public async Task<CaseDetailView?> GetCaseAsync(int caseId, CancellationToken ct = default)
    {
        // The room is read first, so the permission check names the right room. Nothing about the
        // case is returned before that check passes.
        var owningRoomId = await _db.Cases
            .AsNoTracking()
            .Where(c => c.Id == caseId)
            .Select(c => (int?)c.EvidenceRoomId)
            .FirstOrDefaultAsync(ct);

        if (owningRoomId is null)
        {
            return null;
        }

        if (!(await _authorization.AuthorizeAsync(EmcPermissions.ViewCase, owningRoomId, ct)).IsAllowed)
        {
            // Absent, not forbidden (IAM-018).
            return null;
        }

        var owningCase = await _db.Cases
            .AsNoTracking()
            .Include(c => c.Vouchers).ThenInclude(v => v.Items)
            .Include(c => c.Vouchers).ThenInclude(v => v.DocumentNumberAssignments)
            .FirstOrDefaultAsync(c => c.Id == caseId, ct);

        if (owningCase is null)
        {
            return null;
        }

        var vouchers = owningCase.Vouchers
            .OrderBy(v => v.CreatedAtUtc)
            .Select(v => new VoucherListRow(
                v.Id,
                v.DisplayIdentifier,
                v.HasOfficialDocumentNumber,

                // AR 195-5 2-4h - derived from the items, never a stored column.
                v.DerivedStatus,
                v.Items.Count,
                v.AcquiredAtLocal))
            .ToList();

        return new CaseDetailView(
            owningCase.Id,
            owningCase.CaseControlNumber,
            owningCase.Title,
            owningCase.Synopsis,
            owningCase.EvidenceRoomId,
            owningCase.IsClosed,
            vouchers);
    }

    public async Task<VoucherDetailView?> GetVoucherAsync(int voucherId, CancellationToken ct = default)
    {
        var owningRoomId = await _db.EvidenceVouchers
            .AsNoTracking()
            .Where(v => v.Id == voucherId)
            .Select(v => (int?)v.EvidenceRoomId)
            .FirstOrDefaultAsync(ct);

        if (owningRoomId is null)
        {
            return null;
        }

        if (!(await _authorization.AuthorizeAsync(EmcPermissions.ViewVoucher, owningRoomId, ct)).IsAllowed)
        {
            return null;
        }

        var voucher = await _db.EvidenceVouchers
            .AsNoTracking()
            .Include(v => v.Case)
            .Include(v => v.Items)
            .Include(v => v.DocumentNumberAssignments)
            .Include(v => v.ReviewActions)
            .FirstOrDefaultAsync(v => v.Id == voucherId, ct);

        if (voucher?.Case is null)
        {
            return null;
        }

        var ordered = voucher.Items.OrderBy(i => i.ItemNumber).ToList();

        var items = ordered
            .Select((item, index) => new ItemListRow(
                item.Id,
                item.ItemNumber,

                // AR 195-5 2-3l - POSSIBLE BIOHAZARD derived on render, so it cannot drift.
                item.DescriptionForForm,
                item.Quantity,
                item.SerialNumber,
                item.UniqueDeviceIdentifier,
                item.IsPossibleBiohazard,
                item.IsSealed,
                item.AccountabilityStatus,

                // AR 195-5 2-3d - LAST ITEM after the last listed item.
                index == ordered.Count - 1))
            .ToList();

        // AR 195-5 2-7g - superseded numbers stay recorded and visible. "Current" is simply the
        // most recent assignment, derived rather than stored (AUD-002).
        var currentAssignmentId = voucher.CurrentDocumentNumberAssignment?.Id;

        var numbers = voucher.DocumentNumberAssignments
            .OrderBy(a => a.EnteredAtUtc)
            .Select(a => new DocumentNumberRow(
                a.DocumentNumber, a.EnteredAtUtc, a.Id == currentAssignmentId, a.SupersessionReason))
            .ToList();

        var actorIds = voucher.ReviewActions.Select(a => a.ActorUserId).Distinct().ToList();
        var actorNames = await _db.Users
            .AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);

        var reviewActions = voucher.ReviewActions
            .OrderBy(a => a.OccurredAtUtc)
            .ThenBy(a => a.Id)
            .Select(a => new VoucherReviewActionRow(
                a.Kind, a.ResultingStage,
                actorNames.GetValueOrDefault(a.ActorUserId, "(unknown user)"),
                a.OccurredAtUtc, a.Narrative, a.PaperFormCorrectedAndInitialedAttested))
            .ToList();

        // The policy the custodian will write the number under. Resolved at the voucher's
        // acquisition instant - a fact of the record, not a reading of the clock (AUD-019).
        var policies = await _db.EvidenceRoomNumberingPolicies
            .AsNoTracking()
            .Where(p => p.EvidenceRoomId == voucher.EvidenceRoomId)
            .ToListAsync(ct);

        var policy = policies
                         .Where(p => p.IsEffectiveAt(voucher.AcquiredAtUtc))
                         .OrderByDescending(p => p.EffectiveFrom)
                         .FirstOrDefault()
                     ?? policies.OrderByDescending(p => p.EffectiveFrom).FirstOrDefault()
                     ?? EvidenceRoomNumberingPolicy.Regulatory(voucher.EvidenceRoomId, DateTimeOffset.MinValue);

        return new VoucherDetailView(
            voucher.Id,
            voucher.CaseId,
            voucher.Case.CaseControlNumber,
            voucher.RequestingOfficeCaseNumber,
            voucher.EvidenceRoomId,
            voucher.DisplayIdentifier,
            voucher.TemporaryIdentifier,
            voucher.HasOfficialDocumentNumber,
            voucher.IsSubmitted,
            voucher.AllowsItemEditing,
            voucher.DerivedStatus,
            voucher.ReceivingActivity,
            voucher.ReceivingActivityLocation,
            voucher.ReceivedFrom,
            voucher.AcquiredAtLocal,
            items,
            numbers,
            voucher.ReviewStage,
            reviewActions,
            voucher.SubmittedByUserId,
            policy.Describe(),
            policy.Example(),
            policy.IsRegulatoryLayout,
            policy.IsAwaitingValidation);
    }

    public async Task<int?> GetReadableItemEvidenceRoomIdAsync(int itemId, CancellationToken ct = default)
    {
        var owningRoomId = await _db.EvidenceItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => (int?)i.Voucher!.EvidenceRoomId)
            .FirstOrDefaultAsync(ct);

        if (owningRoomId is null)
        {
            return null;
        }

        return (await _authorization.AuthorizeAsync(
                EmcPermissions.ViewEvidenceHistory, owningRoomId, ct)).IsAllowed
            ? owningRoomId
            : null;
    }

    private async Task<List<int>> GetReadableRoomIdsAsync(string permission, CancellationToken ct)
    {
        var readable = new List<int>();

        foreach (var roomId in _currentUser.AccessibleEvidenceRoomIds())
        {
            if ((await _authorization.AuthorizeAsync(permission, roomId, ct)).IsAllowed)
            {
                readable.Add(roomId);
            }
        }

        return readable;
    }
}
