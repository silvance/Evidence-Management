using Emc.Domain.Common;

namespace Emc.Domain.Documents;

public enum RenderJobStatus
{
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public enum RenderRunOutcome
{
    Succeeded = 1,
    Failed = 2
}

/// <summary>Why a render attempt failed, as a CATEGORY. Never a message: a PDF parser's error text is untrusted.</summary>
public enum RenderFailureCategory
{
    None = 0,
    Timeout = 1,
    RendererCrashed = 2,
    MalformedPdf = 3,
    ResourceLimitExceeded = 4,
    DocumentUnavailable = 5,
    RendererUnavailable = 6,
    Unexpected = 7
}

/// <summary>
/// A request to render a source document's pages. A WORK record, leased by the worker under
/// optimistic concurrency exactly like an OCR job (DOC-014). Created by the web process at
/// receipt - the web process never parses the PDF - and again by a person retrying a failed
/// render. What a render produced is never here; that is the immutable <see cref="DocumentRenderRun"/>.
/// </summary>
public sealed class DocumentRenderJob : Entity, IConcurrencyStamped
{
    public const int DefaultMaxAttempts = 3;

    private DocumentRenderJob() { }

    public DocumentRenderJob(int sourceDocumentId, int evidenceRoomId, int requestedByUserId, DateTimeOffset requestedAtUtc)
    {
        SourceDocumentId = Guard.Positive(sourceDocumentId, "DOC-014", "Source document");
        EvidenceRoomId = Guard.Positive(evidenceRoomId, "DOC-014", "Evidence room");
        RequestedByUserId = Guard.Positive(requestedByUserId, "DOC-014", "Requesting user");
        RequestedAtUtc = AccountabilityTime.Normalize(requestedAtUtc);
        Status = RenderJobStatus.Queued;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>For a document received in the same unit of work: the job is saved with it, in one transaction.</summary>
    public DocumentRenderJob(SourceDocument document, int requestedByUserId, DateTimeOffset requestedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        SourceDocumentId = document.Id;
        EvidenceRoomId = Guard.Positive(document.EvidenceRoomId, "DOC-014", "Evidence room");
        RequestedByUserId = Guard.Positive(requestedByUserId, "DOC-014", "Requesting user");
        RequestedAtUtc = AccountabilityTime.Normalize(requestedAtUtc);
        Status = RenderJobStatus.Queued;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public int SourceDocumentId { get; private set; }
    public SourceDocument? Document { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public int RequestedByUserId { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public RenderJobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LeasedByWorkerId { get; private set; }
    public DateTimeOffset? LeaseExpiresUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    public RenderFailureCategory LastFailureCategory { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    public bool IsOpen => Status is RenderJobStatus.Queued or RenderJobStatus.Running;

    public bool IsLeaseExpired(DateTimeOffset now)
        => Status == RenderJobStatus.Running && LeaseExpiresUtc is { } expiry && expiry <= now;

    public bool CanBeLeased(DateTimeOffset now) => Status == RenderJobStatus.Queued || IsLeaseExpired(now);

    public void Lease(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int maxAttempts = DefaultMaxAttempts)
    {
        workerId = Guard.NotBlank(workerId, "DOC-014", "Worker id");
        if (!CanBeLeased(now))
        {
            throw new DomainRuleViolationException("DOC-014", $"Render job {Id} is {Status} and cannot be leased.");
        }

        if (Attempts >= maxAttempts)
        {
            Status = RenderJobStatus.Failed;
            FinishedAtUtc = AccountabilityTime.Normalize(now);
            LastFailureCategory = RenderFailureCategory.Unexpected;
            LeasedByWorkerId = null;
            LeaseExpiresUtc = null;
            ConcurrencyStamp = Guid.NewGuid();
            throw new DomainRuleViolationException("DOC-014", $"Render job {Id} has exhausted its {maxAttempts} attempts.");
        }

        Status = RenderJobStatus.Running;
        Attempts++;
        LeasedByWorkerId = workerId;
        LeaseExpiresUtc = AccountabilityTime.Normalize(now.Add(leaseDuration));
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Complete(string workerId, DateTimeOffset now)
    {
        RequireLeaseHeldBy(workerId);
        Status = RenderJobStatus.Completed;
        FinishedAtUtc = AccountabilityTime.Normalize(now);
        LastFailureCategory = RenderFailureCategory.None;
        LeasedByWorkerId = null;
        LeaseExpiresUtc = null;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>Timeout and crash requeue while attempts remain; malformed input and limits are final.</summary>
    public void Fail(string workerId, DateTimeOffset now, RenderFailureCategory category, int maxAttempts = DefaultMaxAttempts)
    {
        RequireLeaseHeldBy(workerId);
        if (category == RenderFailureCategory.None)
        {
            throw new ArgumentException("A failure needs a category.", nameof(category));
        }

        LastFailureCategory = category;
        LeasedByWorkerId = null;
        LeaseExpiresUtc = null;
        ConcurrencyStamp = Guid.NewGuid();

        var transient = category is RenderFailureCategory.Timeout or RenderFailureCategory.RendererCrashed or RenderFailureCategory.RendererUnavailable;
        if (transient && Attempts < maxAttempts)
        {
            Status = RenderJobStatus.Queued;
            return;
        }

        Status = RenderJobStatus.Failed;
        FinishedAtUtc = AccountabilityTime.Normalize(now);
    }

    private void RequireLeaseHeldBy(string workerId)
    {
        if (Status != RenderJobStatus.Running || !string.Equals(LeasedByWorkerId, workerId, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("DOC-014", $"Render job {Id} is not leased by worker '{workerId}'.");
        }
    }
}

/// <summary>
/// One attempt to render a source document: which renderer, when, how it ended, and - on
/// success - the page images it produced. IMMUTABLE (DOC-015). A failed attempt stays on record
/// and does not prevent a later successful one; "is this document rendered" is derived from the
/// latest successful run, never stored on the document.
/// </summary>
public sealed class DocumentRenderRun : Entity, IAppendOnly
{
    private readonly List<DocumentRenderPage> _pages = [];

    private DocumentRenderRun() { }

    public DocumentRenderRun(
        int renderJobId, int sourceDocumentId, string workerId, string rendererVersion,
        DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc, RenderRunOutcome outcome, RenderFailureCategory failureCategory,
        int? pageCount, int renderDpi)
    {
        RenderJobId = Guard.Positive(renderJobId, "DOC-015", "Render job");
        SourceDocumentId = Guard.Positive(sourceDocumentId, "DOC-015", "Source document");
        WorkerId = Guard.NotBlank(workerId, "DOC-015", "Worker id");
        RendererVersion = Guard.NotBlank(rendererVersion, "DOC-015", "Renderer version");
        StartedAtUtc = AccountabilityTime.Normalize(startedAtUtc);
        CompletedAtUtc = AccountabilityTime.Normalize(completedAtUtc);
        if (CompletedAtUtc < StartedAtUtc)
        {
            throw new DomainRuleViolationException("DOC-015", "A run cannot complete before it started.");
        }

        Outcome = outcome;
        FailureCategory = failureCategory;
        if (outcome == RenderRunOutcome.Succeeded && failureCategory != RenderFailureCategory.None)
        {
            throw new DomainRuleViolationException("DOC-015", "A successful run has no failure category.");
        }

        if (outcome == RenderRunOutcome.Failed && failureCategory == RenderFailureCategory.None)
        {
            throw new DomainRuleViolationException("DOC-015", "A failed run names its failure category.");
        }

        if (outcome == RenderRunOutcome.Succeeded && (pageCount is null || pageCount < 1))
        {
            throw new DomainRuleViolationException("DOC-015", "A successful run states the page count.");
        }

        PageCount = pageCount;
        RenderDpi = Guard.Positive(renderDpi, "DOC-015", "DPI");
    }

    public int RenderJobId { get; private set; }
    public int SourceDocumentId { get; private set; }
    public string WorkerId { get; private set; } = string.Empty;
    public string RendererVersion { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public RenderRunOutcome Outcome { get; private set; }
    public RenderFailureCategory FailureCategory { get; private set; }

    /// <summary>Pages the renderer found; null when it could not open the document.</summary>
    public int? PageCount { get; private set; }
    public int RenderDpi { get; private set; }

    public IReadOnlyList<DocumentRenderPage> Pages => _pages.AsReadOnly();

    public DocumentRenderPage AddPage(int pageNumber, int widthPx, int heightPx, string storageKey, string sha256, long contentLength)
    {
        if (Outcome != RenderRunOutcome.Succeeded)
        {
            throw new DomainRuleViolationException("DOC-015", "A failed run carries no pages.");
        }

        if (_pages.Any(p => p.PageNumber == pageNumber))
        {
            throw new DomainRuleViolationException("DOC-005", $"Page {pageNumber} has already been rendered on this run.");
        }

        var page = new DocumentRenderPage(this, pageNumber, widthPx, heightPx, storageKey, sha256, contentLength);
        _pages.Add(page);
        return page;
    }
}

/// <summary>
/// One rendered page image: a server-generated raster, so the browser is never handed the PDF
/// itself to interpret (DOC-005). Belongs to the render run that produced it. Immutable.
/// </summary>
public sealed class DocumentRenderPage : Entity, IAppendOnly
{
    private DocumentRenderPage() { }

    internal DocumentRenderPage(DocumentRenderRun run, int pageNumber, int widthPx, int heightPx, string storageKey, string sha256, long contentLength)
    {
        RenderRunId = run.Id;
        Run = run;
        PageNumber = Guard.Positive(pageNumber, "DOC-005", "Page number");
        WidthPx = Guard.Positive(widthPx, "DOC-005", "Width");
        HeightPx = Guard.Positive(heightPx, "DOC-005", "Height");
        StorageKey = SourceDocument.ValidateStorageKey(storageKey);
        Sha256 = SourceDocument.ValidateSha256(sha256);
        ContentLength = contentLength;
    }

    public int RenderRunId { get; private set; }
    public DocumentRenderRun? Run { get; private set; }
    public int PageNumber { get; private set; }
    public int WidthPx { get; private set; }
    public int HeightPx { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string Sha256 { get; private set; } = string.Empty;
    public long ContentLength { get; private set; }
}
