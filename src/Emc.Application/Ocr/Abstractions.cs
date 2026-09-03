using Emc.Domain.Ocr;

namespace Emc.Application.Ocr;

/// <summary>One word the engine read: text, confidence 0-100, and its box on the image it was given.</summary>
public sealed record OcrWord(string Text, decimal Confidence, int Left, int Top, int Width, int Height, int BlockIndex, int ParagraphIndex, int LineIndex, int WordIndex);

/// <summary>The engine's output for one image. Words in reading order; the image size they refer to.</summary>
public sealed record OcrPageResult(IReadOnlyList<OcrWord> Words, int ImageWidth, int ImageHeight)
{
    public IEnumerable<IGrouping<(int Block, int Paragraph, int Line), OcrWord>> Lines
        => Words.GroupBy(w => (w.BlockIndex, w.ParagraphIndex, w.LineIndex));
}

/// <summary>Orientation detection: the clockwise rotation (0, 90, 180, 270) that would put the page upright.</summary>
public sealed record OrientationResult(int RotateClockwiseDegrees, decimal Confidence);

/// <summary>A model file the engine loaded, identified by content hash so a run says exactly what read it.</summary>
public sealed record OcrModelInfo(string ModelId, string Sha256)
{
    public override string ToString() => $"{ModelId}@sha256:{Sha256}";
}

/// <summary>
/// A local OCR engine. Runs with NO network access, loads models only from the configured local
/// path, and reports failures as categories: nothing an implementation throws or returns may
/// contain the image's text. Implementations are constructed at worker start and must fail
/// there, explicitly, when the engine or a model is missing (Phase 12).
/// </summary>
public interface IOcrEngine
{
    string EngineName { get; }
    string EngineVersion { get; }
    IReadOnlyList<OcrModelInfo> Models { get; }

    /// <summary>"eng@sha256:...;osd@sha256:..." for the run record.</summary>
    string ModelIdentifiers => string.Join(';', Models.Select(m => m.ToString()));

    Task<OrientationResult> DetectOrientationAsync(byte[] png, CancellationToken ct = default);

    Task<OcrPageResult> RecognizeAsync(byte[] png, CancellationToken ct = default);
}

/// <summary>An engine failure, by category. Carries no content. Message text is fixed per category.</summary>
public sealed class OcrEngineException : Exception
{
    public OcrEngineException(OcrFailureCategory category, Exception? inner = null)
        : base($"OCR engine failure: {category}.", inner)
    {
        Category = category;
    }

    public OcrFailureCategory Category { get; }
}

/// <summary>What preprocessing did to a page before recognition, and the image it produced.</summary>
public sealed record PreprocessedImage(byte[] Png, int Width, int Height, int RotationAppliedDegrees, double DeskewAppliedDegrees, int Dpi);

/// <summary>
/// Turns a rendered page into what the engine should see: upright (from the engine's orientation
/// detection), deskewed by the small angle a scanner introduces, grayscale, contrast-normalized,
/// at the engine's preferred DPI. Deterministic for a given Version.
/// </summary>
public interface IImagePreprocessor
{
    /// <summary>Recorded on every run; bump when the algorithm changes.</summary>
    string Version { get; }

    PreprocessedImage Preprocess(byte[] png, int sourceDpi, int rotateClockwiseDegrees, CancellationToken ct = default);
}

/// <summary>A page as recognized: the engine's words plus the image geometry they refer to.</summary>
public sealed record RecognizedPage(int PageNumber, OcrPageResult Result, PreprocessedImage Image);

/// <summary>A field a template mapper extracted from a recognized page.</summary>
public sealed record ExtractedFieldCandidate(
    string FieldKey, int PageNumber, string RawText, string? NormalizedCandidate, decimal Confidence,
    int Left, int Top, int Width, int Height);

/// <summary>
/// Knows one form's layout. Identification looks at the recognized pages and says whether this
/// is that form; mapping turns recognized words into named fields. Templates are ordered;
/// the first that identifies wins; a fallback that always identifies must come last.
/// </summary>
public interface IFormTemplateMapper
{
    string TemplateId { get; }

    /// <summary>A score in 0..1; at or above <see cref="IdentificationThreshold"/> is a match.</summary>
    decimal Identify(IReadOnlyList<RecognizedPage> pages);

    decimal IdentificationThreshold { get; }

    IReadOnlyList<ExtractedFieldCandidate> Map(IReadOnlyList<RecognizedPage> pages);
}

/// <summary>Worker and engine settings. Section "Ocr". Nothing here is a URL.</summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>Full path to the Tesseract executable. Required for the worker; the web application never uses it.</summary>
    public string EnginePath { get; set; } = string.Empty;

    /// <summary>Folder holding eng.traineddata and osd.traineddata, installed from the dependency bundle.</summary>
    public string TessdataPath { get; set; } = string.Empty;

    /// <summary>Private working folder for the worker's per-job temporary files. Created if missing; ACL'd to the worker account.</summary>
    public string WorkRoot { get; set; } = string.Empty;

    /// <summary>Language model id(s), '+'-separated in Tesseract's form. Only eng is bundled.</summary>
    public string Languages { get; set; } = "eng";

    public int PageTimeoutSeconds { get; set; } = 60;
    public int JobTimeoutSeconds { get; set; } = 600;
    public int LeaseSeconds { get; set; } = 900;
    public int PollSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = OcrJob.DefaultMaxAttempts;

    /// <summary>The engine reads printed text best near 300 DPI; pages are rendered at the document store's DPI and scaled.</summary>
    public int TargetDpi { get; set; } = 300;

    /// <summary>Largest image handed to the engine, in pixels, after scaling.</summary>
    public long MaxPixelsPerPage { get; set; } = 40_000_000;

    /// <summary>Most pages a single job will process; the rest are reported as not processed.</summary>
    public int MaxPagesPerJob { get; set; } = 50;

    /// <summary>Identifies this worker in job leases and run records. Defaults to machine name and process id.</summary>
    public string? WorkerId { get; set; }
}
