using Emc.Domain.Common;

namespace Emc.Domain.Ocr;

/// <summary>
/// What one execution of the OCR engine over one source document produced. IMMUTABLE: a run is
/// a fact about what a named engine, model set and preprocessing version read from a named
/// document at a time. A re-run is a new run; nothing about an old one changes (OCR-004).
///
/// A run names its engine, engine version, model identifiers (with hashes) and preprocessing
/// version so that an engine or model change is an auditable difference between runs, never a
/// silent difference in what was read. A failed run carries a failure CATEGORY and no text.
/// </summary>
public sealed class OcrRun : Entity, IAppendOnly
{
    private readonly List<ExtractedField> _fields = [];

    private OcrRun() { }

    public OcrRun(
        int ocrJobId,
        int sourceDocumentId,
        string workerId,
        string engineName,
        string engineVersion,
        string modelIdentifiers,
        string preprocessingVersion,
        string? templateId,
        bool templateIdentified,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        OcrRunOutcome outcome,
        OcrFailureCategory failureCategory,
        int pagesProcessed)
    {
        OcrJobId = Guard.Positive(ocrJobId, "OCR-012", "Job");
        SourceDocumentId = Guard.Positive(sourceDocumentId, "OCR-012", "Source document");
        WorkerId = Guard.NotBlank(workerId, "OCR-012", "Worker id");
        EngineName = Guard.NotBlank(engineName, "OCR-012", "Engine name");
        EngineVersion = Guard.NotBlank(engineVersion, "OCR-012", "Engine version");
        ModelIdentifiers = Guard.NotBlank(modelIdentifiers, "OCR-012", "Model identifiers");
        PreprocessingVersion = Guard.NotBlank(preprocessingVersion, "OCR-012", "Preprocessing version");
        TemplateId = Guard.TrimToNull(templateId);
        TemplateIdentified = templateIdentified;
        StartedAtUtc = AccountabilityTime.Normalize(startedAtUtc);
        CompletedAtUtc = AccountabilityTime.Normalize(completedAtUtc);
        if (CompletedAtUtc < StartedAtUtc)
        {
            throw new DomainRuleViolationException("OCR-012", "A run cannot complete before it started.");
        }

        Outcome = outcome;
        FailureCategory = failureCategory;
        if (outcome == OcrRunOutcome.Succeeded && failureCategory != OcrFailureCategory.None)
        {
            throw new DomainRuleViolationException("OCR-012", "A successful run has no failure category.");
        }

        if (outcome == OcrRunOutcome.Failed && failureCategory == OcrFailureCategory.None)
        {
            throw new DomainRuleViolationException("OCR-012", "A failed run names its failure category.");
        }

        PagesProcessed = pagesProcessed < 0 ? throw new DomainRuleViolationException("OCR-012", "Pages processed cannot be negative.") : pagesProcessed;
    }

    public int OcrJobId { get; private set; }
    public int SourceDocumentId { get; private set; }
    public string WorkerId { get; private set; } = string.Empty;
    public string EngineName { get; private set; } = string.Empty;
    public string EngineVersion { get; private set; } = string.Empty;

    /// <summary>e.g. "eng@sha256:...;osd@sha256:..." - what was loaded, by content hash.</summary>
    public string ModelIdentifiers { get; private set; } = string.Empty;
    public string PreprocessingVersion { get; private set; } = string.Empty;

    /// <summary>The template the pages were mapped with, or null when the run failed before mapping.</summary>
    public string? TemplateId { get; private set; }

    /// <summary>False when the fallback (generic lines) was used because no template matched.</summary>
    public bool TemplateIdentified { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public OcrRunOutcome Outcome { get; private set; }
    public OcrFailureCategory FailureCategory { get; private set; }
    public int PagesProcessed { get; private set; }

    public IReadOnlyList<ExtractedField> Fields => _fields;

    public ExtractedField AddField(
        string fieldKey,
        int pageNumber,
        string rawText,
        string? normalizedCandidate,
        decimal confidence,
        int left, int top, int width, int height)
    {
        if (Outcome != OcrRunOutcome.Succeeded)
        {
            throw new DomainRuleViolationException("OCR-012", "A failed run carries no fields.");
        }

        var field = new ExtractedField(this, fieldKey, pageNumber, rawText, normalizedCandidate, confidence, left, top, width, height);
        _fields.Add(field);
        return field;
    }
}

/// <summary>
/// One value the engine read, where it read it, how sure it was, and whether a person must look
/// at it before it is used for anything. Immutable: the raw text is what the engine said and is
/// never edited (OCR-004). Verification is a separate append-only record.
/// </summary>
public sealed class ExtractedField : Entity, IAppendOnly
{
    public const int MaxRawTextLength = 4000;

    private readonly List<FieldVerification> _verifications = [];

    private ExtractedField() { }

    internal ExtractedField(
        OcrRun run, string fieldKey, int pageNumber, string rawText, string? normalizedCandidate,
        decimal confidence, int left, int top, int width, int height)
    {
        Run = run;
        OcrRunId = run.Id;
        if (!OcrFieldCatalog.IsValidKey(fieldKey))
        {
            throw new DomainRuleViolationException("OCR-013", $"'{fieldKey}' is not a field key (Section.Field or Section[n].Field).");
        }

        FieldKey = fieldKey;
        PageNumber = Guard.Positive(pageNumber, "OCR-013", "Page number");
        RawText = rawText ?? string.Empty;
        if (RawText.Length > MaxRawTextLength)
        {
            throw new DomainRuleViolationException("OCR-013", $"Raw text exceeds {MaxRawTextLength} characters.");
        }

        NormalizedCandidate = Guard.TrimToNull(normalizedCandidate);
        Confidence = confidence;
        Band = ConfidenceBanding.Band(confidence);
        if (left < 0 || top < 0 || width < 0 || height < 0)
        {
            throw new DomainRuleViolationException("OCR-013", "A bounding box has non-negative coordinates.");
        }

        Left = left; Top = top; Width = width; Height = height;
        IsHighConsequence = OcrFieldCatalog.IsHighConsequence(fieldKey);

        // OCR-002 / OCR-003. High-consequence: always. Otherwise: anything the engine was not
        // highly confident about. A High-band, low-consequence field is prepopulated and
        // reviewable, but a person is not FORCED to it.
        RequiresVerification = IsHighConsequence || Band != ConfidenceBand.High;
    }

    public int OcrRunId { get; private set; }
    public OcrRun Run { get; private set; } = null!;
    public string FieldKey { get; private set; } = string.Empty;
    public int PageNumber { get; private set; }

    /// <summary>Exactly what the engine produced. Never edited.</summary>
    public string RawText { get; private set; } = string.Empty;

    /// <summary>A normalization of the raw text in the field's expected shape (a document number's digits, a date), offered as a candidate. Never authoritative.</summary>
    public string? NormalizedCandidate { get; private set; }
    public decimal Confidence { get; private set; }
    public ConfidenceBand Band { get; private set; }

    /// <summary>Bounding box in pixels on the rendered page image the run processed.</summary>
    public int Left { get; private set; }
    public int Top { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsHighConsequence { get; private set; }
    public bool RequiresVerification { get; private set; }

    public IReadOnlyList<FieldVerification> Verifications => _verifications;

    /// <summary>The latest verification is the current one; earlier ones stay (append-only).</summary>
    public FieldVerification? CurrentVerification
        => _verifications.OrderByDescending(v => v.VerifiedAtUtc).ThenByDescending(v => v.Id).FirstOrDefault();

    /// <summary>
    /// The value a person has vouched for, if any. Verification never rewrites RawText: the
    /// accepted reading is the raw text, a correction is the verifier's text, a manual entry is
    /// the verifier's text from the paper, and NotApplicable has no value.
    /// </summary>
    public string? VerifiedValue
        => CurrentVerification switch
        {
            null => null,
            { Decision: FieldVerificationDecision.AcceptedAsRead } => NormalizedCandidate ?? RawText,
            { Decision: FieldVerificationDecision.NotApplicable } => null,
            var v => v.EnteredValue
        };

    public bool IsVerified => CurrentVerification is not null;

    public FieldVerification RecordVerification(
        int verifiedByUserId, DateTimeOffset verifiedAtUtc, FieldVerificationDecision decision, string? enteredValue, string? note)
    {
        var verification = new FieldVerification(this, verifiedByUserId, verifiedAtUtc, decision, enteredValue, note);
        _verifications.Add(verification);
        return verification;
    }
}

/// <summary>
/// A person's decision about one extracted field. Append-only: a second look is a second row.
/// This is TranscriptionVerification (CorrectionCategory), not an accountability correction:
/// when a verifier reads "8G4P2K8" as "8G4P2K3", the paper was always right.
/// </summary>
public sealed class FieldVerification : Entity, IAppendOnly
{
    public const int MaxValueLength = 4000;

    private FieldVerification() { }

    internal FieldVerification(
        ExtractedField field, int verifiedByUserId, DateTimeOffset verifiedAtUtc,
        FieldVerificationDecision decision, string? enteredValue, string? note)
    {
        Field = field;
        ExtractedFieldId = field.Id;
        VerifiedByUserId = Guard.Positive(verifiedByUserId, "OCR-014", "Verifier");
        VerifiedAtUtc = AccountabilityTime.Normalize(verifiedAtUtc);
        Decision = decision;
        EnteredValue = Guard.TrimToNull(enteredValue);
        Note = Guard.TrimToNull(note);

        switch (decision)
        {
            case FieldVerificationDecision.CorrectedByVerifier when EnteredValue is null:
                throw new DomainRuleViolationException("OCR-014", "A correction states the value the paper shows.");
            case FieldVerificationDecision.CorrectedByVerifier when string.Equals(EnteredValue, field.NormalizedCandidate ?? field.RawText, StringComparison.Ordinal):
                throw new DomainRuleViolationException("OCR-014", "The entered value is what the engine read; record it as accepted as read.");
            case FieldVerificationDecision.AcceptedAsRead when field.Band == ConfidenceBand.LowOrUnreadable:
                throw new DomainRuleViolationException("OCR-014", "A low or unreadable field is not accepted as read; it is entered from the paper or marked unreadable.");
            case FieldVerificationDecision.AcceptedAsRead when EnteredValue is not null:
                throw new DomainRuleViolationException("OCR-014", "Accepting as read takes no entered value.");
            case FieldVerificationDecision.NotApplicable when EnteredValue is not null:
                throw new DomainRuleViolationException("OCR-014", "A not-applicable field takes no value.");
        }

        if (EnteredValue?.Length > MaxValueLength)
        {
            throw new DomainRuleViolationException("OCR-014", $"Entered value exceeds {MaxValueLength} characters.");
        }
    }

    public int ExtractedFieldId { get; private set; }
    public ExtractedField Field { get; private set; } = null!;
    public int VerifiedByUserId { get; private set; }
    public DateTimeOffset VerifiedAtUtc { get; private set; }
    public FieldVerificationDecision Decision { get; private set; }

    /// <summary>The verifier's value for a correction or a manual entry; null otherwise.</summary>
    public string? EnteredValue { get; private set; }
    public string? Note { get; private set; }
}
