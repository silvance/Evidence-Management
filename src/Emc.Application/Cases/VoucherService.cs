using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Application.Items;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Cases;

public sealed record CreateVoucherRequest(
    int CaseId,
    string ReceivingActivity,
    string ReceivingActivityLocation,
    string ReceivedFrom,
    DateTimeOffset AcquiredAtLocal,
    bool IsRequestForAssistance,
    string? RequestingOfficeCaseNumber);

public sealed record AddItemRequest(
    int VoucherId,
    string Description,
    string? Quantity,
    string? SerialNumber,
    string? UniqueDeviceIdentifier,
    bool IsPossibleBiohazard,
    bool IsFungible,
    bool IsSealed,
    string? SealDescription);

public sealed record UpdateItemRequest(
    int ItemId,
    string Description,
    string? Quantity,
    string? SerialNumber,
    string? UniqueDeviceIdentifier,
    bool IsPossibleBiohazard,
    bool IsFungible,
    bool IsSealed,
    string? SealDescription);

/// <summary>AR 195-5 2-3g - the custodian returns the form, stating what is wrong with it.</summary>
public sealed record ReturnVoucherForCorrectionRequest(int VoucherId, string ErrorsIdentified);

/// <summary>
/// AR 195-5 2-3g - the submitting agent records the correction. The attestation is that the
/// PAPER form was corrected and initialed; EMC supplies no initials (VCH-019, AUD-013).
/// </summary>
public sealed record RecordAgentCorrectionRequest(
    int VoucherId, string WhatWasCorrected, bool PaperFormCorrectedAndInitialedAttested);

public interface IVoucherService
{
    Task<OperationResult<int>> CreateDraftAsync(CreateVoucherRequest request, CancellationToken ct = default);
    Task<OperationResult<int>> AddItemAsync(AddItemRequest request, CancellationToken ct = default);
    Task<OperationResult> UpdateItemAsync(UpdateItemRequest request, CancellationToken ct = default);
    Task<OperationResult> RemoveItemAsync(int itemId, CancellationToken ct = default);
    Task<OperationResult> SubmitForCustodianIntakeAsync(int voucherId, CancellationToken ct = default);

    /// <summary>AR 195-5 2-3g. Custodian only; before acceptance only.</summary>
    Task<OperationResult> ReturnForCorrectionAsync(ReturnVoucherForCorrectionRequest request, CancellationToken ct = default);

    /// <summary>AR 195-5 2-3g. The submitting agent only.</summary>
    Task<OperationResult> RecordAgentCorrectionAsync(RecordAgentCorrectionRequest request, CancellationToken ct = default);

    /// <summary>Puts the corrected form before the custodian again. The submitting agent only.</summary>
    Task<OperationResult> ResubmitForCustodianIntakeAsync(int voucherId, CancellationToken ct = default);
}

/// <summary>
/// Draft voucher lifecycle: create, add/edit/remove items, submit for custodian intake.
///
/// AR 195-5 2-3b — the agent who first acquired the evidence prepares the DA Form 4137.
/// AR 195-5 2-3g — the custodian reviews the submitted form and has the submitting agent
/// "correct and initial all errors", which is why items are editable only while the voucher is a
/// draft (VCH-010, invariant I-10). After submission, change happens through a correction so the
/// original entry remains readable (AR 195-5 2-5b(5)).
/// </summary>
public sealed class VoucherService : IVoucherService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IItemEventRecorder _events;
    private readonly IClock _clock;
    private readonly ITemporaryIdentifierAllocator _temporaryIdentifiers;

    public VoucherService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        ICurrentUser currentUser,
        IAuditRecorder audit,
        IItemEventRecorder events,
        IClock clock,
        ITemporaryIdentifierAllocator temporaryIdentifiers)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _events = events;
        _clock = clock;
        _temporaryIdentifiers = temporaryIdentifiers;
    }

    public async Task<OperationResult<int>> CreateDraftAsync(
        CreateVoucherRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var owningCase = await _db.Cases.FirstOrDefaultAsync(c => c.Id == request.CaseId, ct);
        if (owningCase is null)
        {
            return OperationResult<int>.Failure("Case not found.", "CASE-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.CreateDraftVoucher, owningCase.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return await DenyAsync<int>(decision, nameof(EvidenceVoucher), null, ct);
        }

        // VCH-003 / EMC-002. AR 195-5 2-4c reserves assignment of the official document number to
        // the evidence custodian, by order of precedence from the evidence ledger. EMC issues an
        // unmistakably temporary identifier until the custodian transcribes the real one.
        // VCH-024. Database-backed and retried on contention; never COUNT + 1.
        var temporaryIdentifier = await _temporaryIdentifiers.AllocateAsync(
            owningCase.EvidenceRoomId, DateOnly.FromDateTime(request.AcquiredAtLocal.Date), ct);

        EvidenceVoucher voucher;
        try
        {
            voucher = new EvidenceVoucher(
                caseId: owningCase.Id,
                evidenceRoomId: owningCase.EvidenceRoomId,
                temporaryIdentifier: temporaryIdentifier,

                // AR 195-5 2-3b — the preparing agent is the one who first acquired the evidence.
                preparedByUserId: _currentUser.UserId,
                receivingActivity: request.ReceivingActivity,
                receivingActivityLocation: request.ReceivingActivityLocation,
                receivedFrom: request.ReceivedFrom,
                acquiredAtUtc: request.AcquiredAtLocal.ToUniversalTime(),
                acquiredAtLocal: request.AcquiredAtLocal,
                createdByUserId: _currentUser.UserId,
                createdAtUtc: _clock.UtcNow);

            // AR 195-5 2-3b — evidence collected in response to a request for assistance records
            // BOTH the seizing and requesting offices' numbers (CASE-002).
            if (request.IsRequestForAssistance)
            {
                voucher.MarkAsRequestForAssistance(request.RequestingOfficeCaseNumber ?? string.Empty);
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        _db.EvidenceVouchers.Add(voucher);

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceVoucher), temporaryIdentifier.ToString(),
            newValue: "Draft voucher created");

        await _db.SaveChangesAsync(ct);
        return OperationResult<int>.Success(voucher.Id);
    }

    public async Task<OperationResult<int>> AddItemAsync(
        AddItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var voucher = await LoadVoucherWithItemsAsync(request.VoucherId, ct);
        if (voucher is null)
        {
            return OperationResult<int>.Failure("Voucher not found.", "VCH-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.EditDraftVoucher, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return await DenyAsync<int>(decision, nameof(EvidenceItem), null, ct);
        }

        EvidenceItem item;
        try
        {
            item = voucher.AddItem(
                description: request.Description,
                quantity: request.Quantity,
                serialNumber: request.SerialNumber,
                uniqueDeviceIdentifier: request.UniqueDeviceIdentifier,
                isPossibleBiohazard: request.IsPossibleBiohazard,
                isFungible: request.IsFungible,
                isSealed: request.IsSealed,
                sealDescription: request.SealDescription);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceItem), $"{voucher.DisplayIdentifier}/{item.ItemNumber}",
            newValue: "Item added to draft voucher");

        await _db.SaveChangesAsync(ct);

        return OperationResult<int>.Success(item.Id, DescriptionWarnings(item));
    }

    public async Task<OperationResult> UpdateItemAsync(
        UpdateItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = await _db.EvidenceItems
            .Include(i => i.Voucher!).ThenInclude(v => v.Items)
            .FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);

        if (item?.Voucher is null)
        {
            return OperationResult.Failure("Item not found.", "ITEM-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.EditDraftVoucher, item.Voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return (await DenyAsync<bool>(decision, nameof(EvidenceItem), item.Id.ToString(), ct))
                .ToUntyped();
        }

        try
        {
            item.UpdateDetails(
                description: request.Description,
                quantity: request.Quantity,
                serialNumber: request.SerialNumber,
                uniqueDeviceIdentifier: request.UniqueDeviceIdentifier,
                isPossibleBiohazard: request.IsPossibleBiohazard,
                isFungible: request.IsFungible,
                isSealed: request.IsSealed,
                sealDescription: request.SealDescription);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceItem), $"{item.Voucher.DisplayIdentifier}/{item.ItemNumber}",
            newValue: "Draft item updated");

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success([.. DescriptionWarnings(item)]);
    }

    public async Task<OperationResult> RemoveItemAsync(int itemId, CancellationToken ct = default)
    {
        var item = await _db.EvidenceItems
            .Include(i => i.Voucher!).ThenInclude(v => v.Items)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item?.Voucher is null)
        {
            return OperationResult.Failure("Item not found.", "ITEM-001");
        }

        var voucher = item.Voucher;

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.EditDraftVoucher, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return (await DenyAsync<bool>(decision, nameof(EvidenceItem), itemId.ToString(), ct))
                .ToUntyped();
        }

        var identifier = $"{voucher.DisplayIdentifier}/{item.ItemNumber}";

        try
        {
            // Removes the item and renumbers the remainder so numbering stays contiguous from 1
            // (AR 195-5 2-3d, invariant I-01).
            voucher.RemoveItem(item);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        _db.EvidenceItems.Remove(item);

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceItem), identifier,
            previousValue: "Item present on draft voucher",
            newValue: "Item removed from draft voucher");

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    /// <summary>
    /// AR 195-5 2-4a — submit for custodian intake. The evidence itself is due to the custodian
    /// no later than the first working day after acquisition; EMC records the submission and does
    /// not attempt to compute that deadline until "working day" is defined (DEC-02, VCH-016).
    /// </summary>
    public async Task<OperationResult> SubmitForCustodianIntakeAsync(
        int voucherId, CancellationToken ct = default)
    {
        var voucher = await LoadVoucherWithItemsAsync(voucherId, ct);
        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "VCH-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.SubmitVoucherForIntake, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return (await DenyAsync<bool>(decision, nameof(EvidenceVoucher), voucherId.ToString(), ct))
                .ToUntyped();
        }

        var now = _clock.UtcNow;

        try
        {
            voucher.SubmitForCustodianIntake(_currentUser.UserId, now);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        // Every item moves Draft -> Acquired -> AwaitingCustodian, and each transition is recorded
        // as a StatusEvent so the workflow itself is auditable (invariant I-22).
        foreach (var item in voucher.Items.OrderBy(i => i.ItemNumber))
        {
            await AppendStatusAsync(
                item, AccountabilityStatus.Acquired,
                "Evidence acquired by the preparing agent (AR 195-5 2-1a, 2-3b).", now, ct);

            await AppendStatusAsync(
                item, AccountabilityStatus.AwaitingCustodian,
                "Voucher submitted for evidence custodian intake (AR 195-5 2-4a).", now, ct);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceVoucher), voucher.DisplayIdentifier,
            previousValue: "Draft",
            newValue: "Awaiting custodian intake");

        await _db.SaveChangesAsync(ct);

        return OperationResult.Success(
            "AR 195-5 para 2-4a: except in unusual circumstances, the physical evidence is "
            + "released to the evidence custodian no later than the first working day after it "
            + "is acquired. Submitting this voucher does not transfer physical custody.");
    }

    public async Task<OperationResult> ReturnForCorrectionAsync(
        ReturnVoucherForCorrectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var voucher = await LoadVoucherWithItemsAsync(request.VoucherId, ct);
        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "VCH-001");
        }

        // A custodian act (2-3g: "Evidence custodians will review ..."), so it needs an active
        // appointment, not merely a custodian role (IAM-005).
        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.ReturnVoucherForCorrection, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return (await DenyAsync<bool>(decision, nameof(EvidenceVoucher), voucher.DisplayIdentifier, ct))
                .ToUntyped();
        }

        var now = _clock.UtcNow;

        try
        {
            voucher.ReturnForCorrection(_currentUser.UserId, request.ErrorsIdentified, now);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        // Each item goes back to the agent's custody state. The transition is on the record with
        // the custodian's reason, so the item's own history shows the return (invariant I-22).
        foreach (var item in voucher.Items.OrderBy(i => i.ItemNumber))
        {
            if (item.AccountabilityStatus == AccountabilityStatus.AwaitingCustodian)
            {
                await AppendStatusAsync(
                    item, AccountabilityStatus.Acquired,
                    "Voucher returned by the evidence custodian to the submitting agent to correct "
                    + "and initial errors (AR 195-5 2-3g): " + request.ErrorsIdentified.Trim(),
                    now, ct);
            }
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceVoucher), voucher.DisplayIdentifier,
            previousValue: "Awaiting custodian intake",
            newValue: "Returned to submitting agent for correction",
            reason: request.ErrorsIdentified);

        await _db.SaveChangesAsync(ct);

        return OperationResult.Success(
            "AR 195-5 para 2-3g: the submitting agent corrects and initials all errors on the "
            + "DA Form 4137, then resubmits it. Items on this voucher may be edited until then.");
    }

    public async Task<OperationResult> RecordAgentCorrectionAsync(
        RecordAgentCorrectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var voucher = await LoadVoucherWithItemsAsync(request.VoucherId, ct);
        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "VCH-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.EditDraftVoucher, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return (await DenyAsync<bool>(decision, nameof(EvidenceVoucher), voucher.DisplayIdentifier, ct))
                .ToUntyped();
        }

        try
        {
            // The domain checks that the caller IS the submitting agent (2-3g), not merely
            // someone with agent permissions in the room.
            voucher.RecordCorrectionBySubmittingAgent(
                _currentUser.UserId,
                request.WhatWasCorrected,
                request.PaperFormCorrectedAndInitialedAttested,
                _clock.UtcNow);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceVoucher), voucher.DisplayIdentifier,
            previousValue: "Returned to submitting agent for correction",
            newValue: "Corrected by submitting agent; paper form corrected and initialed (attested)",
            reason: request.WhatWasCorrected);

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    public async Task<OperationResult> ResubmitForCustodianIntakeAsync(
        int voucherId, CancellationToken ct = default)
    {
        var voucher = await LoadVoucherWithItemsAsync(voucherId, ct);
        if (voucher is null)
        {
            return OperationResult.Failure("Voucher not found.", "VCH-001");
        }

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.SubmitVoucherForIntake, voucher.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            return (await DenyAsync<bool>(decision, nameof(EvidenceVoucher), voucher.DisplayIdentifier, ct))
                .ToUntyped();
        }

        var now = _clock.UtcNow;

        try
        {
            voucher.Resubmit(_currentUser.UserId, now);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult.Failure(ex.Message, ex.RequirementId);
        }

        // Items added while the voucher was with the agent start from Draft; the rest were
        // returned to Acquired. All end up awaiting the custodian again.
        foreach (var item in voucher.Items.OrderBy(i => i.ItemNumber))
        {
            if (item.AccountabilityStatus == AccountabilityStatus.Draft)
            {
                await AppendStatusAsync(
                    item, AccountabilityStatus.Acquired,
                    "Evidence acquired by the preparing agent (AR 195-5 2-1a, 2-3b).", now, ct);
            }

            if (item.AccountabilityStatus == AccountabilityStatus.Acquired)
            {
                await AppendStatusAsync(
                    item, AccountabilityStatus.AwaitingCustodian,
                    "Corrected voucher resubmitted for evidence custodian intake (AR 195-5 2-3g, 2-4a).",
                    now, ct);
            }
        }

        _audit.Record(
            AuditEventType.AccountabilityActionRecorded,
            nameof(EvidenceVoucher), voucher.DisplayIdentifier,
            previousValue: "Corrected by submitting agent",
            newValue: "Resubmitted for custodian intake");

        await _db.SaveChangesAsync(ct);
        return OperationResult.Success();
    }

    private async Task AppendStatusAsync(
        EvidenceItem item,
        AccountabilityStatus target,
        string reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var from = item.AccountabilityStatus;
        item.TransitionTo(target);

        await _events.AppendAsync(
            item,
            new StatusEvent(
                fromStatus: from,
                toStatus: target,
                reason: reason,
                occurredAtLocal: now,
                recordedAtUtc: now,
                recordedByUserId: _currentUser.UserId),
            ct);
    }

    /// <summary>
    /// AR 195-5 2-3d prohibits descriptions "based on supposition or suspicions". EMC WARNS
    /// rather than blocks: a word list cannot reliably distinguish a prohibited inference from a
    /// legitimate description, and blocking an accurate description on a keyword match would be
    /// worse than the problem it solves (ITEM-003).
    /// </summary>
    private static string[] DescriptionWarnings(EvidenceItem item)
    {
        var phrases = item.DetectSuppositionPhrases();
        if (phrases.Count == 0)
        {
            return [];
        }

        return
        [
            $"The description contains \"{string.Join("\", \"", phrases)}\". AR 195-5 para 2-3d "
            + "requires descriptions to include only descriptive information and not phrases "
            + "based on supposition or suspicion (for example \"suspected to be marijuana\"). "
            + "Review the wording before the voucher is submitted."
        ];
    }

    private Task<EvidenceVoucher?> LoadVoucherWithItemsAsync(int voucherId, CancellationToken ct)
        => _db.EvidenceVouchers
            .Include(v => v.Items).ThenInclude(i => i.Events)
            .Include(v => v.DocumentNumberAssignments)
            .Include(v => v.ReviewActions)
            .FirstOrDefaultAsync(v => v.Id == voucherId, ct);

    private async Task<OperationResult<T>> DenyAsync<T>(
        AuthorizationDecision decision, string recordType, string? recordId, CancellationToken ct)
    {
        _audit.Record(
            AuditEventType.PermissionDenied,
            recordType, recordId, reason: decision.Reason, succeeded: false);

        await _db.SaveChangesAsync(ct);
        return OperationResult<T>.Failure(decision.Reason!, decision.RequirementId);
    }
}

internal static class OperationResultExtensions
{
    public static OperationResult ToUntyped<T>(this OperationResult<T> result)
        => result.Succeeded
            ? OperationResult.Success([.. result.Warnings])
            : OperationResult.Failure(result.Error!, result.RequirementId);
}
