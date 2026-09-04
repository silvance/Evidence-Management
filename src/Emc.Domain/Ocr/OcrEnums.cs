namespace Emc.Domain.Ocr;

/// <summary>Lifecycle of a request to run OCR over a source document. A job is a work record, not accountability history, and is mutable.</summary>
public enum OcrJobStatus
{
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public enum OcrRunOutcome
{
    Succeeded = 1,
    Failed = 2
}

/// <summary>
/// Why a run failed, as a CATEGORY. Never a message: an engine's error text can quote the
/// image's content, and the run record, the log and the job row must carry none of it.
/// </summary>
public enum OcrFailureCategory
{
    None = 0,
    EngineUnavailable = 1,
    ModelMissing = 2,
    Timeout = 3,
    EngineCrashed = 4,
    InvalidImage = 5,
    TemplateNotIdentified = 6,
    ResourceLimitExceeded = 7,
    DocumentUnavailable = 8,
    Unexpected = 9,

    /// <summary>An installed engine binary or model file does not hash to the approved value (OCR-017). A start-up failure; never a per-job one.</summary>
    ArtifactNotApproved = 10
}

/// <summary>
/// OCR-002. High: prepopulated, still reviewable. Medium: prepopulated and prominently flagged.
/// LowOrUnreadable: no guess is offered; the value is entered by a person from the paper.
/// </summary>
public enum ConfidenceBand
{
    High = 1,
    Medium = 2,
    LowOrUnreadable = 3
}

public enum FieldVerificationDecision
{
    /// <summary>The verifier compared the extraction to the scan and it is what the paper says.</summary>
    AcceptedAsRead = 1,

    /// <summary>The paper says something else; the verifier typed it. The raw extraction is kept (OCR-004).</summary>
    CorrectedByVerifier = 2,

    /// <summary>The field cannot be read from the scan; the value was entered from the physical document or left blank.</summary>
    UnreadableManualEntry = 3,

    /// <summary>The field does not apply to this document (an empty block on the form, a page without it).</summary>
    NotApplicable = 4
}
