using Emc.Application.Abstractions;
using Emc.Domain.Common;
using Emc.Domain.Configuration;

namespace Emc.Application.Audit;

/// <summary>
/// Writes security/administrative audit entries.
///
/// This is NOT the accountability record — that lives in ItemEvent and its subtypes. Keeping the
/// two separate is deliberate (AUD-009, docs/architecture.md §4.5): they have different
/// consumers, different retention and different sensitivity.
///
/// Entries here carry identifiers and outcomes. They must never carry investigative content —
/// evidence descriptions, serial numbers, IMEIs, names of subjects (AUD-010).
/// </summary>
public interface IAuditRecorder
{
    void Record(
        AuditEventType eventType,
        string affectedRecordType,
        string? affectedRecordId,
        string? previousValue = null,
        string? newValue = null,
        string? reason = null,
        bool succeeded = true);
}

public sealed class AuditRecorder : IAuditRecorder
{
    private readonly IEmcDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IRequestContext _requestContext;

    public AuditRecorder(
        IEmcDbContext db, ICurrentUser currentUser, IClock clock, IRequestContext requestContext)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _requestContext = requestContext;
    }

    public void Record(
        AuditEventType eventType,
        string affectedRecordType,
        string? affectedRecordId,
        string? previousValue = null,
        string? newValue = null,
        string? reason = null,
        bool succeeded = true)
    {
        var auditEvent = new AuditEvent(
            eventType: eventType,
            actingUserId: _currentUser.IsAuthenticated ? _currentUser.UserId : null,
            actingUserName: _currentUser.IsAuthenticated ? _currentUser.DisplayName : "(unauthenticated)",
            affectedRecordType: affectedRecordType,
            affectedRecordId: affectedRecordId,
            occurredAtUtc: _clock.UtcNow,
            previousValue: previousValue,
            newValue: newValue,
            reason: reason,
            succeeded: succeeded)
            .WithRequestContext(_requestContext.SourceAddress, _requestContext.CorrelationId);

        _db.AuditEvents.Add(auditEvent);
    }
}

/// <summary>Per-request context for audit correlation. Never carries investigative content.</summary>
public interface IRequestContext
{
    string? SourceAddress { get; }
    string? CorrelationId { get; }
}
