using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Ocr;
using Emc.Infrastructure.Documents;
using Emc.Infrastructure.Ocr;
using Microsoft.Extensions.Options;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The REAL engine, as an external process, on synthetic pages. Runs wherever Tesseract 5 and
/// its models are installed locally - the development image here, the air-gapped build host
/// after the bundle's engine is installed - and is SKIPPED, visibly, elsewhere. Every page it
/// reads is generated in the test: no real form is anywhere near this.
/// Requirements: OCR-006, OCR-012, SEC-014, Phase 12 (offline OCR).
/// </summary>
public class TesseractEngineTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "emc-tests", "ocr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    private OcrOptions Options(string? tessdata = null, int pageTimeoutSeconds = 60) => new()
    {
        EnginePath = TesseractFactAttribute.EnginePath!,
        TessdataPath = tessdata ?? TesseractFactAttribute.TessdataPath!,
        WorkRoot = _work,
        PageTimeoutSeconds = pageTimeoutSeconds
    };

    private static byte[] RenderedPage(string text, int dpi = 150)
        => new PdfiumRasterizer().Render(SyntheticPdf.SinglePage(text), 1, dpi).Png;

    [TesseractFact]
    public void TheEngineStartsOnlyWithItsBinaryAndEveryModelPresent_Locally()
    {
        var engine = new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.Equal("tesseract", engine.EngineName);
        Assert.Matches(@"^\d+\.\d+", engine.EngineVersion);
        Assert.Contains(engine.Models, m => m.ModelId == "eng" && m.Sha256.Length == 64);
        Assert.Contains(engine.Models, m => m.ModelId == "osd");
        Assert.Contains("eng@sha256:", ((IOcrEngine)engine).ModelIdentifiers, StringComparison.Ordinal);

        // Phase 12: a missing model is an explicit start-up failure, not a queue that never drains.
        var empty = Path.Combine(_work, "no-models");
        Directory.CreateDirectory(empty);
        var missing = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options(empty))));
        Assert.Equal(OcrFailureCategory.ModelMissing, missing.Category);

        var noEngine = Assert.Throws<OcrEngineException>(() => new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(
            new OcrOptions { EnginePath = Path.Combine(_work, "absent.exe"), TessdataPath = TesseractFactAttribute.TessdataPath!, WorkRoot = _work })));
        Assert.Equal(OcrFailureCategory.EngineUnavailable, noEngine.Category);
    }

    [TesseractFact]
    public async Task ASyntheticPrintedPageIsReadWithWordConfidencesAndBoxes()
    {
        var engine = new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options()));
        var png = new SkiaImagePreprocessor(300).Preprocess(RenderedPage("TEST DA FORM 4137 FICTITIOUS TEST-CI-2026-0001"), 150, 0).Png;

        var result = await engine.RecognizeAsync(png);

        Assert.True(result.ImageWidth > 1000 && result.ImageHeight > result.ImageWidth);
        var texts = result.Words.Select(w => w.Text).ToList();
        Assert.Contains("TEST", texts);
        Assert.Contains("4137", texts);
        Assert.Contains("FICTITIOUS", texts);
        Assert.All(result.Words, w => { Assert.InRange(w.Confidence, 0m, 100m); Assert.True(w.Width > 0 && w.Height > 0); });
        Assert.True(result.Words.Where(w => w.Text is "TEST" or "FORM" or "4137").All(w => w.Confidence >= 60m));

        // Nothing was left behind in the work root.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_work).Where(e => !e.EndsWith("no-models", StringComparison.Ordinal)));
    }

    [TesseractFact]
    public async Task AnUpsideDownPageIsReadAfterRotation_AndTheVoteFindsIt()
    {
        var engine = new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options()));
        var prep = new SkiaImagePreprocessor(300);
        var upsideDown = prep.Preprocess(RenderedPage("UPRIGHT TEXT READS HERE NOW FOR THE ORIENTATION VOTE"), 150, 180).Png;

        var wrong = await engine.RecognizeAsync(upsideDown);
        var righted = await engine.RecognizeAsync(prep.Preprocess(upsideDown, 300, 180).Png);

        Assert.True(OcrJobProcessor.OrientationScore(righted) > OcrJobProcessor.OrientationScore(wrong),
            "the upright orientation must score higher than the inverted one");
        Assert.Contains(righted.Words, w => w.Text == "ORIENTATION");
    }

    [TesseractFact]
    public async Task ASkewedPageIsDeskewedBeforeRecognition()
    {
        var engine = new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options()));
        var prep = new SkiaImagePreprocessor(300);

        // Introduce 3 degrees of skew with the preprocessor's own rotation path, then let it
        // estimate and remove it.
        using var source = SkiaSharp.SKBitmap.Decode(RenderedPage("SKEWED TEST PAGE WITH SEVERAL WORDS ON THE LINE"));
        using var skewed = new SkiaSharp.SKBitmap(source.Width, source.Height);
        using (var canvas = new SkiaSharp.SKCanvas(skewed))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.Translate(source.Width / 2f, source.Height / 2f);
            canvas.RotateDegrees(3);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0, SkiaSharp.SKSamplingOptions.Default);
        }

        var estimated = SkiaImagePreprocessor.EstimateSkewDegrees(skewed);
        Assert.InRange(Math.Abs(estimated), 2.0, 4.0);

        using var image = SkiaSharp.SKImage.FromBitmap(skewed);
        var skewedPng = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
        var corrected = prep.Preprocess(skewedPng, 150, 0);
        Assert.InRange(Math.Abs(corrected.DeskewAppliedDegrees), 2.0, 4.0);

        var result = await engine.RecognizeAsync(corrected.Png);
        Assert.Contains(result.Words, w => w.Text == "SKEWED");
    }

    [TesseractFact]
    public async Task ATimeoutKillsTheEngineProcess()
    {
        var engine = new TesseractProcessOcrEngine(Microsoft.Extensions.Options.Options.Create(Options()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(RenderedPage("TIMEOUT"), cts.Token));
        Assert.Empty(Directory.EnumerateDirectories(_work));
    }

    [Fact]
    public void TsvParsingKeepsWordsOnly_AndClampsConfidence()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "1\t1\t0\t0\t0\t0\t0\t0\t1275\t1650\t-1\t\n"
            + "4\t1\t1\t1\t1\t0\t151\t183\t544\t26\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t151\t183\t84\t26\t94.969635\tTEST\n"
            + "5\t1\t1\t1\t1\t2\t247\t184\t43\t24\t-1\t\n"
            + "5\t1\t1\t1\t2\t1\t151\t270\t60\t23\t100.5\tNEXT\n";

        var result = TesseractProcessOcrEngine.ParseTsv(tsv);

        Assert.Equal(1275, result.ImageWidth);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("TEST", result.Words[0].Text);
        Assert.Equal(94.97m, result.Words[0].Confidence);
        Assert.Equal(100m, result.Words[1].Confidence);
        Assert.Equal(2, result.Lines.Count());
    }

    [Fact]
    public void TheWorkerConfigurationNamesNoNetworkLocation()
    {
        // OCR-006. The worker's settings are paths; no URL can be configured for the engine or a model.
        var props = typeof(OcrOptions).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(props, p => p.Contains("Url", StringComparison.OrdinalIgnoreCase) || p.Contains("Endpoint", StringComparison.OrdinalIgnoreCase) || p.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
        var source = File.ReadAllText(Path.Combine(OfflineBuildTests.Root, "src", "Emc.Infrastructure", "Ocr", "TesseractProcessOcrEngine.cs"));
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", source, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", source, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UseShellExecute = true", source, StringComparison.Ordinal);
    }
}

/// <summary>
/// Runs when a local Tesseract 5 install is found: EMC_TESSERACT_PATH / EMC_TESSDATA_PATH, or the
/// usual Linux and Windows install locations. Otherwise the test is skipped with the reason shown.
/// </summary>
public sealed class TesseractFactAttribute : FactAttribute
{
    public static readonly string? EnginePath = Find(
        Environment.GetEnvironmentVariable("EMC_TESSERACT_PATH"),
        "/usr/bin/tesseract", "/usr/local/bin/tesseract",
        @"C:\Program Files\Tesseract-OCR\tesseract.exe");

    public static readonly string? TessdataPath = FindDir(
        Environment.GetEnvironmentVariable("EMC_TESSDATA_PATH"),
        "/usr/share/tesseract-ocr/5/tessdata", "/usr/share/tessdata", "/usr/local/share/tessdata",
        @"C:\Program Files\Tesseract-OCR\tessdata");

    public TesseractFactAttribute()
    {
        if (EnginePath is null || TessdataPath is null || !File.Exists(Path.Combine(TessdataPath, "eng.traineddata")) || !File.Exists(Path.Combine(TessdataPath, "osd.traineddata")))
        {
            Skip = "Tesseract 5 with eng and osd models is not installed locally (set EMC_TESSERACT_PATH and EMC_TESSDATA_PATH). The real-engine tests are SKIPPED on this host.";
        }
    }

    private static string? Find(params string?[] candidates) => candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    private static string? FindDir(params string?[] candidates) => candidates.FirstOrDefault(c => c is not null && Directory.Exists(c));
}
