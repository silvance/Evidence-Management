namespace Emc.Domain.Common;

/// <summary>Base for every persisted entity.</summary>
public abstract class Entity
{
    public int Id { get; protected set; }
}

/// <summary>
/// Marks a record whose accountability history must never be rewritten.
///
/// Modelled on AR 195-5 para 2-5b(5) — an erroneous evidence-ledger entry is voided with a
/// single line drawn through it "so it may still be read" and initialed by the custodian;
/// correction fluid, correction tape, stick-on labels and erasures are prohibited — together
/// with para 1-7c(3), which requires the discovering custodian to immediately inform the
/// supervisor and prepare an MFR stating the error and the corrective action taken.
///
/// AR 195-5 has no general append-only rule for electronic records, because it does not
/// contemplate a general electronic record. This is therefore a DESIGN + CONTROL decision
/// modelled on those paragraphs. See docs/regulatory-requirements.md §6 and §12.
///
/// Enforcement is three-layered (docs/architecture.md §4.2):
///   1. domain    - no public setters on accountability fields;
///   2. persistence - EmcDbContext.SaveChanges rejects Modified and Deleted OUTRIGHT;
///   3. database  - INSTEAD OF UPDATE, DELETE triggers that reject unconditionally.
///
/// INSERT ONLY. There is no permitted UPDATE, not even a narrow one.
///
/// An earlier design allowed exactly one mutation - setting a forward "superseded by" pointer -
/// which forced the database trigger to prove that every OTHER column was unchanged. That is
/// error-prone in a table-per-hierarchy table, and the trigger in fact compared only the columns
/// common to all event types, leaving subtype columns such as StorageLocationPath and
/// PurposeOfChangeOfCustody freely modifiable alongside a legitimate supersession.
///
/// Corrections now use BACKWARD references instead: a CorrectionEvent names the event it
/// corrects, and correction status is DERIVED from the existence of those records. Nothing ever
/// updates the corrected row, so the triggers can be unconditional.
/// </summary>
public interface IAppendOnly;

/// <summary>
/// Optimistic concurrency for mutable aggregates. A GUID stamp rather than a SQL Server
/// rowversion so the same behaviour holds under SQLite in tests (docs/architecture.md §8).
/// </summary>
public interface IConcurrencyStamped
{
    Guid ConcurrencyStamp { get; set; }
}

/// <summary>Abstracted so that time is injectable and tests are deterministic.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>A violated domain rule. Carries the requirement ID for traceability.</summary>
public sealed class DomainRuleViolationException : Exception
{
    public DomainRuleViolationException(string requirementId, string message)
        : base($"[{requirementId}] {message}")
    {
        RequirementId = requirementId;
    }

    /// <summary>Requirement ID from docs/requirements-traceability.md (e.g. "ITEM-002").</summary>
    public string RequirementId { get; }
}

/// <summary>Thrown when something attempts to modify or delete append-only history.</summary>
public sealed class AppendOnlyViolationException : Exception
{
    public AppendOnlyViolationException(string message) : base(message) { }
}

/// <summary>
/// Normalizes timestamps before they are stored and hashed.
///
/// The per-item hash chain (AUD-008) covers every field of an event, timestamps included. If a
/// stored timestamp comes back with less precision than the value that was hashed, the chain
/// breaks and reports tampering that never happened - and the precision depends on the column
/// type and the provider. SQL Server datetimeoffset keeps 100ns; a datetime2(3) column, or
/// EF's DateTimeOffsetToBinaryConverter, keeps milliseconds.
///
/// Rather than let the chain's validity depend on storage precision, accountability timestamps
/// are truncated to whole milliseconds when the event is constructed. Every value is then
/// hashed and stored at the same precision on any provider that keeps at least milliseconds.
///
/// A millisecond is far finer than anything AR 195-5 records: the DA Form 4137 and the evidence
/// ledger use minutes (para 2-5b, "03 SEP 26 09:15").
/// </summary>
public static class AccountabilityTime
{
    private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    public static DateTimeOffset Normalize(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % TicksPerMillisecond), value.Offset);

    public static DateTimeOffset? Normalize(DateTimeOffset? value)
        => value is null ? null : Normalize(value.Value);
}

public static class Guard
{
    public static string NotBlank(string? value, string requirementId, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainRuleViolationException(requirementId, $"{field} is required.");
        }

        return value.Trim();
    }

    public static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static int Positive(int value, string requirementId, string field)
    {
        if (value <= 0)
        {
            throw new DomainRuleViolationException(requirementId, $"{field} must be positive.");
        }

        return value;
    }
}
