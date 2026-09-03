using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Cases;

public sealed record CreateCaseRequest(
    string CaseControlNumber,
    string Title,
    string? Synopsis,
    int EvidenceRoomId);

public interface ICaseService
{
    Task<OperationResult<int>> CreateAsync(CreateCaseRequest request, CancellationToken ct = default);
}

/// <summary>
/// Case creation.
///
/// AR 195-5 2-3b requires the Army CI case control number to be recorded on the DA Form 4137 and
/// the DA Form 4002, so it is the case's identifying key here (CASE-001).
/// </summary>
public sealed class CaseService : ICaseService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;

    public CaseService(
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

    public async Task<OperationResult<int>> CreateAsync(
        CreateCaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var decision = await _authorization.AuthorizeAsync(
            EmcPermissions.CreateCase, request.EvidenceRoomId, ct);

        if (!decision.IsAllowed)
        {
            _audit.Record(
                Domain.Common.AuditEventType.PermissionDenied,
                nameof(Case), null, reason: decision.Reason, succeeded: false);

            await _db.SaveChangesAsync(ct);
            return OperationResult<int>.Failure(decision.Reason!, decision.RequirementId);
        }

        var normalized = request.CaseControlNumber?.Trim() ?? string.Empty;

        var duplicate = await _db.Cases
            .AsNoTracking()
            .AnyAsync(c => c.CaseControlNumber == normalized
                           && c.EvidenceRoomId == request.EvidenceRoomId, ct);

        if (duplicate)
        {
            return OperationResult<int>.Failure(
                $"Case control number '{normalized}' already exists for this evidence room.",
                "CASE-001");
        }

        Case newCase;
        try
        {
            newCase = new Case(
                caseControlNumber: normalized,
                title: request.Title,
                evidenceRoomId: request.EvidenceRoomId,
                createdByUserId: _currentUser.UserId,
                createdAtUtc: _clock.UtcNow);

            newCase.UpdateDetails(request.Title, request.Synopsis);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        _db.Cases.Add(newCase);

        // AUD-009: identifiers only. The case control number identifies the record; the synopsis
        // is investigative content and does not belong in an audit entry.
        _audit.Record(
            Domain.Common.AuditEventType.AccountabilityActionRecorded,
            nameof(Case), normalized, newValue: "Case created");

        await _db.SaveChangesAsync(ct);
        return OperationResult<int>.Success(newCase.Id);
    }
}
