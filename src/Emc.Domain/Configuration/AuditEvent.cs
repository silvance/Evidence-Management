using Emc.Domain.Common;

namespace Emc.Domain.Configuration;

/// <summary>
/// Security and administrative audit.
///
/// Deliberately NARROW and deliberately distinct from the accountability record (AUD-009,
/// docs/architecture.md §4.5). The accountability record lives in ItemEvent and its subtypes;
/// this table records who signed in, who was denied, who changed a role or a configuration
/// setting, who downloaded a source document, who exported data, and who ran an integrity
/// verification.
///
/// Conflating the two is a common and damaging mistake, and so is letting either leak into the
/// diagnostic log: diagnostic logs are rotated, shipped to support staff and read casually, and
/// evidence descriptions must not travel with them (AUD-010).
///
/// Append-only (AUD-001).
/// </summary>
public class AuditEvent : Entity, IAppendOnly
{
    private AuditEvent() { }

    public AuditEvent(
        AuditEventType eventType,
        int? actingUserId,
        string actingUserName,
        string affectedRecordType,
        string? affectedRecordId,
        DateTimeOffset occurredAtUtc,
        string? previousValue = null,
        string? newValue = null,
        string? reason = null,
        bool succeeded = true)
    {
        EventType = eventType;
        ActingUserId = actingUserId;
        ActingUserName = Guard.NotBlank(actingUserName, "IAM-001", "Acting user name");
        AffectedRecordType = Guard.NotBlank(affectedRecordType, "IAM-001", "Affected record type");
        AffectedRecordId = Guard.TrimToNull(affectedRecordId);
        OccurredAtUtc = occurredAtUtc;
        PreviousValue = Guard.TrimToNull(previousValue);
        NewValue = Guard.TrimToNull(newValue);
        Reason = Guard.TrimToNull(reason);
        Succeeded = succeeded;
    }

    public AuditEventType EventType { get; private set; }

    public int? ActingUserId { get; private set; }

    /// <summary>
    /// Denormalized deliberately. A sign-in denial may have no User row, and an audit trail must
    /// remain readable even if the account is later removed.
    /// </summary>
    public string ActingUserName { get; private set; } = string.Empty;

    public string AffectedRecordType { get; private set; } = string.Empty;
    public string? AffectedRecordId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? PreviousValue { get; private set; }
    public string? NewValue { get; private set; }
    public string? Reason { get; private set; }

    public bool Succeeded { get; private set; }

    /// <summary>Client IP or workstation, for security investigation. Never investigative content.</summary>
    public string? SourceAddress { get; private set; }

    /// <summary>Ties an audit entry to a request, and to the diagnostic log for the same request.</summary>
    public string? CorrelationId { get; private set; }

    public AuditEvent WithRequestContext(string? sourceAddress, string? correlationId)
    {
        SourceAddress = Guard.TrimToNull(sourceAddress);
        CorrelationId = Guard.TrimToNull(correlationId);
        return this;
    }
}
