using Emc.Domain.Common;

namespace Emc.Domain.Ocr;

/// <summary>
/// A request to run OCR over one source document. A WORK record: it is leased, retried and
/// completed, so it is mutable under optimistic concurrency. What the engine read is never
/// stored here - that is the immutable <see cref="OcrRun"/>.
///
/// Leasing is by database row: a worker takes a Queued job (or a Running one whose lease has
/// expired, which means the worker holding it died) by writing its id and a lease expiry under
/// the concurrency stamp. Two workers racing for one job: one wins the stamp, the other sees a
/// conflict and moves on. No broker, no queue service, nothing outside SQL Server (Phase 3C).
/// </summary>
public sealed class OcrJob : Entity, IConcurrencyStamped
{
    public const int DefaultMaxAttempts = 3;

    private OcrJob() { }

    public OcrJob(int sourceDocumentId, int evidenceRoomId, int requestedByUserId, DateTimeOffset requestedAtUtc)
    {
        SourceDocumentId = Guard.Positive(sourceDocumentId, "OCR-010", "Source document");
        EvidenceRoomId = Guard.Positive(evidenceRoomId, "OCR-010", "Evidence room");
        RequestedByUserId = Guard.Positive(requestedByUserId, "OCR-010", "Requesting user");
        RequestedAtUtc = AccountabilityTime.Normalize(requestedAtUtc);
        Status = OcrJobStatus.Queued;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int SourceDocumentId { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public int RequestedByUserId { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public OcrJobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LeasedByWorkerId { get; private set; }
    public DateTimeOffset? LeaseExpiresUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>The last failure, as a category (never text).</summary>
    public OcrFailureCategory LastFailureCategory { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    public bool IsOpen => Status is OcrJobStatus.Queued or OcrJobStatus.Running;

    public bool IsLeaseExpired(DateTimeOffset now)
        => Status == OcrJobStatus.Running && LeaseExpiresUtc is { } expiry && expiry <= now;

    public bool CanBeLeased(DateTimeOffset now)
        => Status == OcrJobStatus.Queued || IsLeaseExpired(now);

    public void Lease(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int maxAttempts = DefaultMaxAttempts)
    {
        workerId = Guard.NotBlank(workerId, "OCR-011", "Worker id");
        if (!CanBeLeased(now))
        {
            throw new DomainRuleViolationException("OCR-011", $"Job {Id} is {Status} and cannot be leased.");
        }

        if (Attempts >= maxAttempts)
        {
            // A job that keeps dying with its worker is not retried forever.
            Status = OcrJobStatus.Failed;
            FinishedAtUtc = AccountabilityTime.Normalize(now);
            LastFailureCategory = OcrFailureCategory.Unexpected;
            LeasedByWorkerId = null;
            LeaseExpiresUtc = null;
            ConcurrencyStamp = Guid.NewGuid();
            throw new DomainRuleViolationException("OCR-011", $"Job {Id} has exhausted its {maxAttempts} attempts.");
        }

        Status = OcrJobStatus.Running;
        Attempts++;
        LeasedByWorkerId = workerId;
        LeaseExpiresUtc = AccountabilityTime.Normalize(now.Add(leaseDuration));
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Complete(string workerId, DateTimeOffset now)
    {
        RequireLeaseHeldBy(workerId);
        Status = OcrJobStatus.Completed;
        FinishedAtUtc = AccountabilityTime.Normalize(now);
        LastFailureCategory = OcrFailureCategory.None;
        LeasedByWorkerId = null;
        LeaseExpiresUtc = null;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>
    /// The attempt failed. Transient categories (timeout, engine crash, resource limit) go back
    /// to the queue while attempts remain; everything else is final.
    /// </summary>
    public void Fail(string workerId, DateTimeOffset now, OcrFailureCategory category, int maxAttempts = DefaultMaxAttempts)
    {
        RequireLeaseHeldBy(workerId);
        if (category == OcrFailureCategory.None)
        {
            throw new ArgumentException("A failure needs a category.", nameof(category));
        }

        LastFailureCategory = category;
        LeasedByWorkerId = null;
        LeaseExpiresUtc = null;
        ConcurrencyStamp = Guid.NewGuid();

        var transient = category is OcrFailureCategory.Timeout or OcrFailureCategory.EngineCrashed or OcrFailureCategory.ResourceLimitExceeded;
        if (transient && Attempts < maxAttempts)
        {
            Status = OcrJobStatus.Queued;
            return;
        }

        Status = OcrJobStatus.Failed;
        FinishedAtUtc = AccountabilityTime.Normalize(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new DomainRuleViolationException("OCR-011", $"Job {Id} is {Status} and cannot be cancelled.");
        }

        Status = OcrJobStatus.Cancelled;
        FinishedAtUtc = AccountabilityTime.Normalize(now);
        LeasedByWorkerId = null;
        LeaseExpiresUtc = null;
        ConcurrencyStamp = Guid.NewGuid();
    }

    private void RequireLeaseHeldBy(string workerId)
    {
        if (Status != OcrJobStatus.Running || !string.Equals(LeasedByWorkerId, workerId, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("OCR-011", $"Job {Id} is not leased by worker '{workerId}'.");
        }
    }
}
