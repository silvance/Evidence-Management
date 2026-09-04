using Emc.Application.Ocr;
using Emc.Application.Ocr.DaForm4137;
using Emc.Domain.Ocr;
using Emc.Infrastructure.Documents;
using Emc.Infrastructure.Ocr;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The DA Form 4137 template mapper over the real engine and SYNTHETIC form pages: clean,
/// rotated, flipped, skewed, low-contrast, multi-page, 2-3h and 2-3i continuation pages, an
/// unreadable block, and a document number that conflicts between faces. Requirements: OCR-003,
/// OCR-007, OCR-008, OCR-009, OCR-013, OCR-015.
/// </summary>
public class DaForm4137MappingTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "emc-tests", "map-" + Guid.NewGuid().ToString("N"));
    private readonly DaForm4137TemplateMapper _mapper = new();

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    private TesseractProcessOcrEngine Engine() => new(Options.Create(new OcrOptions
    {
        EnginePath = TesseractFactAttribute.EnginePath!, TessdataPath = TesseractFactAttribute.TessdataPath!, WorkRoot = _work
    }));

    /// <summary>Renders every page, optionally distorts the raster, preprocesses, recognizes.</summary>
    private async Task<List<RecognizedPage>> RecognizeAsync(byte[] pdf, Func<SKBitmap, SKBitmap>? distort = null, int rotateForPreprocess = 0)
    {
        var engine = Engine();
        var prep = new SkiaImagePreprocessor(300);
        var raster = new PdfiumRasterizer();
        var pages = new List<RecognizedPage>();
        var count = raster.GetPageCount(pdf);
        for (var p = 1; p <= count; p++)
        {
            var png = raster.Render(pdf, p, 150).Png;
            if (distort is not null)
            {
                using var bmp = SKBitmap.Decode(png);
                using var changed = distort(bmp);
                using var img = SKImage.FromBitmap(changed);
                png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            }

            var image = prep.Preprocess(png, 150, rotateForPreprocess);
            var result = await engine.RecognizeAsync(image.Png);
            pages.Add(new RecognizedPage(p, result, image));
        }

        return pages;
    }

    private static string? Value(IReadOnlyList<ExtractedFieldCandidate> fields, string key)
        => fields.FirstOrDefault(f => f.FieldKey == key) is { } f ? (f.NormalizedCandidate ?? f.RawText) : null;

    [TesseractFact]
    public async Task ACleanFrontAndBackAreIdentifiedAndMapped()
    {
        var pages = await RecognizeAsync(SyntheticDaForm4137.Build(new SyntheticDaForm4137.Options
        {
            BackCustody = [new("1-3", "05 SEP 26", "BAKER, TEST C. SA", "USACIL TEST", "LABORATORY EXAMINATION")],
            FinalDisposalAction = "RETURNED TO OWNER - TEST", FinalDisposalAuthority = "TEST SAC"
        }));

        Assert.True(_mapper.Identify(pages) >= _mapper.IdentificationThreshold, $"identification score {_mapper.Identify(pages)}");
        Assert.Equal(DaForm4137Face.Front, DaForm4137TemplateMapper.Classify(pages[0]));
        Assert.Equal(DaForm4137Face.Back, DaForm4137TemplateMapper.Classify(pages[1]));

        var fields = _mapper.Map(pages);

        Assert.Equal("007-26", Value(fields, OcrFieldCatalog.DocumentNumber));
        Assert.Contains("2026-0001", Value(fields, OcrFieldCatalog.CaseControlNumber) ?? "", StringComparison.Ordinal);
        Assert.Contains("TEST EVIDENCE ROOM", Value(fields, OcrFieldCatalog.ReceivingActivity) ?? "", StringComparison.Ordinal);
        Assert.Contains("SMITH, TEST A.", Value(fields, OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived) ?? "", StringComparison.Ordinal);
        Assert.Equal("03 SEP 26 0915", Value(fields, OcrFieldCatalog.DateTimeObtained));

        Assert.Equal("1", Value(fields, "Item[1].ItemNumber"));
        Assert.Equal("2", Value(fields, "Item[2].ItemNumber"));
        Assert.Equal("3", Value(fields, "Item[3].ItemNumber"));
        Assert.Contains("MOBILE TELEPHONE", Value(fields, "Item[1].Description") ?? "", StringComparison.Ordinal);
        Assert.Equal("000000000000001", Value(fields, "Item[1].UniqueDeviceIdentifier"));
        Assert.Equal("TESTSERIAL000002", Value(fields, "Item[2].SerialNumber"));
        Assert.Contains("100", Value(fields, "Item[3].Quantity") ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(fields, f => f.FieldKey == "Item[4].ItemNumber"); // LAST ITEM stopped the table

        Assert.Equal("1-3", Value(fields, "Custody[1].ItemNumber"));
        Assert.Equal("03 SEP 26", Value(fields, "Custody[1].Date"));
        Assert.Contains("SMITH", Value(fields, "Custody[1].ReleasedByName") ?? "", StringComparison.Ordinal);
        Assert.Contains("JONES", Value(fields, "Custody[1].ReceivedByName") ?? "", StringComparison.Ordinal);
        Assert.Contains("BAKER", Value(fields, "Custody[2].ReceivedByName") ?? "", StringComparison.Ordinal);
        Assert.Equal("05 SEP 26", Value(fields, "Custody[3].Date")); // continues on the back
        Assert.Equal(2, fields.Count(f => f.FieldKey == OcrFieldCatalog.DocumentNumber)); // front and back
        Assert.Empty(DaForm4137TemplateMapper.Conflicts(fields));
        Assert.Contains("RETURNED TO OWNER", Value(fields, OcrFieldCatalog.DispositionAction) ?? "", StringComparison.Ordinal);

        // Every high-consequence field the mapper emitted is flagged for verification by the domain.
        var run = new OcrRun(1, 1, 1, "w", "tesseract", "5", "m", "p", DaForm4137TemplateMapper.Id, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, OcrRunOutcome.Succeeded, OcrFailureCategory.None, 2);
        foreach (var c in fields) run.AddField(c.FieldKey, c.PageNumber, c.RawText, c.NormalizedCandidate, c.Confidence, c.Left, c.Top, c.Width, c.Height);
        Assert.All(run.Fields.Where(f => f.IsHighConsequence), f => Assert.True(f.RequiresVerification));
        Assert.Contains(run.Fields, f => f.FieldKey == OcrFieldCatalog.DocumentNumber && f.IsHighConsequence);
        Assert.Contains(run.Fields, f => f.FieldKey == "Custody[1].ReceivedByName" && f.IsHighConsequence);
    }

    [TesseractFact]
    public async Task RotatedFlippedSkewedAndFaintPagesMapToTheSameDocumentNumber()
    {
        var pdf = SyntheticDaForm4137.Build(new SyntheticDaForm4137.Options { IncludeBack = false });

        // 180° (the vertical flip of a two-sided scan) - undone by the orientation step.
        var flipped = await RecognizeAsync(pdf, bmp => Rotate(bmp, 180), rotateForPreprocess: 180);
        Assert.Equal("007-26", Value(_mapper.Map(flipped), OcrFieldCatalog.DocumentNumber));

        // 90° - a page fed sideways.
        var sideways = await RecognizeAsync(pdf, bmp => Rotate(bmp, 90), rotateForPreprocess: 270);
        Assert.Equal("007-26", Value(_mapper.Map(sideways), OcrFieldCatalog.DocumentNumber));

        // 2.5° skew - removed by the deskew step.
        var skewed = await RecognizeAsync(pdf, bmp => Skew(bmp, 2.5f));
        Assert.Equal("007-26", Value(_mapper.Map(skewed), OcrFieldCatalog.DocumentNumber));
        Assert.Contains("2026-0001", Value(_mapper.Map(skewed), OcrFieldCatalog.CaseControlNumber) ?? "", StringComparison.Ordinal);

        // Low contrast (a faint photocopy) plus speckle - lifted by the contrast stretch.
        var faint = await RecognizeAsync(pdf, bmp => Fade(bmp, 0.35f, noise: true));
        Assert.Equal("007-26", Value(_mapper.Map(faint), OcrFieldCatalog.DocumentNumber));

        // A different DPI: 100 instead of 150 - the mapper is anchored on labels, not pixels.
        var raster = new PdfiumRasterizer();
        var lowDpi = new SkiaImagePreprocessor(300).Preprocess(raster.Render(pdf, 1, 100).Png, 100, 0);
        var lowDpiPage = new RecognizedPage(1, await Engine().RecognizeAsync(lowDpi.Png), lowDpi);
        Assert.Equal("007-26", Value(_mapper.Map([lowDpiPage]), OcrFieldCatalog.DocumentNumber));
    }

    [TesseractFact]
    public async Task ContinuationPagesAreRecognized_ItemsAndCustodyContinueNumbering()
    {
        // 2-3h: items 4-5 on a Continuation of Description of Articles page; 2-3i: a new form
        // carrying "Continuation of Chain of Custody, dated ..." with custody rows 3-4.
        var pdf = SyntheticDaForm4137.Build(new SyntheticDaForm4137.Options
        {
            ContinuationItems = [new(4, "1", "ONE TEST NOTEBOOK, RED"), new(5, "2", "TWO TEST KEYS ON A RING")],
            ChainContinuation = [new("1-5", "06 SEP 26", "BAKER, TEST C. SA", "USACIL TEST", "LABORATORY EXAMINATION"), new("1-5", "20 SEP 26", "USACIL TEST", "BAKER, TEST C. SA", "RETURNED FROM LABORATORY")]
        });
        var pages = await RecognizeAsync(pdf);
        Assert.Equal(4, pages.Count);
        Assert.Equal(DaForm4137Face.Front, DaForm4137TemplateMapper.Classify(pages[0]));
        Assert.Equal(DaForm4137Face.Back, DaForm4137TemplateMapper.Classify(pages[1]));
        Assert.Equal(DaForm4137Face.ContinuationOfDescriptionOfArticles, DaForm4137TemplateMapper.Classify(pages[2]));
        Assert.Equal(DaForm4137Face.ContinuationOfChainOfCustody, DaForm4137TemplateMapper.Classify(pages[3]));

        var fields = _mapper.Map(pages);
        Assert.Equal("4", Value(fields, "Item[4].ItemNumber"));
        Assert.Equal("5", Value(fields, "Item[5].ItemNumber"));
        Assert.Equal(3, fields.Single(f => f.FieldKey == "Item[4].ItemNumber").PageNumber);
        Assert.Contains("NOTEBOOK", Value(fields, "Item[4].Description") ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(fields, f => f.FieldKey == "Item[6].ItemNumber");

        // The continuation page carries the case number and header entries "as shown on the original".
        Assert.Contains(fields, f => f.FieldKey == OcrFieldCatalog.CaseControlNumber && f.PageNumber == 3);
        Assert.Equal("06 SEP 26", Value(fields, "Custody[3].Date"));
        Assert.Equal("20 SEP 26", Value(fields, "Custody[4].Date"));
        Assert.Equal(4, fields.Single(f => f.FieldKey == "Custody[4].Date").PageNumber);
        Assert.DoesNotContain(fields, f => f.FieldKey.StartsWith("Item[", StringComparison.Ordinal) && f.PageNumber == 4); // the 2-3i form lists no items
    }

    [TesseractFact]
    public async Task AnUnreadableBlockIsEmittedEmptyAtZeroConfidence_NeverGuessed()
    {
        var pdf = SyntheticDaForm4137.Build(new SyntheticDaForm4137.Options { IncludeBack = false, UnreadableBlock = "DocumentNumber" });
        var fields = _mapper.Map(await RecognizeAsync(pdf));

        var docNo = fields.Single(f => f.FieldKey == OcrFieldCatalog.DocumentNumber);
        Assert.True(docNo.RawText.Length == 0 || docNo.NormalizedCandidate is null, $"an unreadable block must not yield a candidate, got '{docNo.RawText}'");
        Assert.True(docNo.Confidence < ConfidenceBanding.MediumThreshold);

        var run = new OcrRun(1, 1, 1, "w", "tesseract", "5", "m", "p", DaForm4137TemplateMapper.Id, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, OcrRunOutcome.Succeeded, OcrFailureCategory.None, 1);
        var field = run.AddField(docNo.FieldKey, docNo.PageNumber, docNo.RawText, docNo.NormalizedCandidate, docNo.Confidence, docNo.Left, docNo.Top, docNo.Width, docNo.Height);
        Assert.Equal(ConfidenceBand.LowOrUnreadable, field.Band);
        Assert.True(field.RequiresVerification);
        Assert.Throws<Emc.Domain.Common.DomainRuleViolationException>(() => field.RecordVerification(1, DateTimeOffset.UtcNow, FieldVerificationDecision.AcceptedAsRead, null, null));

        // The other blocks were still read.
        Assert.Contains("TEST EVIDENCE ROOM", Value(fields, OcrFieldCatalog.ReceivingActivity) ?? "", StringComparison.Ordinal);
    }

    [TesseractFact]
    public async Task AConflictingDocumentNumberBetweenFacesIsSurfaced_NotResolved()
    {
        var pdf = SyntheticDaForm4137.Build(new SyntheticDaForm4137.Options { BackDocumentNumber = "008-26" });
        var fields = _mapper.Map(await RecognizeAsync(pdf));

        var numbers = fields.Where(f => f.FieldKey == OcrFieldCatalog.DocumentNumber).OrderBy(f => f.PageNumber).ToList();
        Assert.Equal(2, numbers.Count);
        Assert.Equal("007-26", numbers[0].NormalizedCandidate);
        Assert.Equal("008-26", numbers[1].NormalizedCandidate);

        var conflicts = DaForm4137TemplateMapper.Conflicts(fields);
        Assert.Contains(conflicts, c => c.FieldKey == OcrFieldCatalog.DocumentNumber && c.Values.Count == 2);
    }

    [Fact]
    public void NormalizersOfferCandidatesInTheFieldShape_AndNothingElse()
    {
        Assert.Equal("007-26", DaForm4137TemplateMapper.Normalizers.DocumentNumber("OOO7-26"));
        Assert.Equal("123-26", DaForm4137TemplateMapper.Normalizers.DocumentNumber("123 – 26"));
        Assert.Null(DaForm4137TemplateMapper.Normalizers.DocumentNumber("no number here"));
        Assert.Equal("03 SEP 26", DaForm4137TemplateMapper.Normalizers.Date("3 Sep 2026"));
        Assert.Null(DaForm4137TemplateMapper.Normalizers.Date("03 XYZ 26"));
        Assert.Equal("1-3", DaForm4137TemplateMapper.Normalizers.ItemNumberList("1 - 3"));
        Assert.Null(DaForm4137TemplateMapper.Normalizers.ItemNumberList("SMITH"));
        Assert.Equal("TESTSERIAL000002", DaForm4137TemplateMapper.Normalizers.SerialFromDescription("LAPTOP, S/N TESTSERIAL000002, GRAY"));
        Assert.Equal("000000000000001", DaForm4137TemplateMapper.Normalizers.ImeiFromDescription("PHONE IMEI 000000 000000 001"));
        Assert.Null(DaForm4137TemplateMapper.Normalizers.SerialFromDescription("ONE TEST BAG"));
    }

    [Fact]
    public void LabelMatchingToleratesOcrNoise()
    {
        Assert.True(TextMatching.WordMatches("RECEIVING", "RECEIVlNG"));
        Assert.True(TextMatching.WordMatches("ACTIVITY", "ACTIVITY."));
        Assert.False(TextMatching.WordMatches("DATE", "NAME"));
        var line = new[] { W("DESCRlPTION"), W("0F"), W("ARTICLES"), W("(Include") };
        Assert.Equal((0, 2), TextMatching.FindPhrase(line, "DESCRIPTION OF ARTICLES"));
        Assert.Null(TextMatching.FindPhrase([W("DA"), W("FORM"), W("4l37")], "DA FORM 4137")); // digits must match exactly
        Assert.Equal((0, 0), TextMatching.FindPhrase([W("ITEMNO."), W("DATE")], "ITEM NO"));       // merged tokens
        Assert.Equal((1, 5), TextMatching.FindPhrase([W("BY"), W("PURPOSE"), W("OF"), W("CHANGE"), W("OF"), W("CUSTODY")], "PURPOSE OF CHANGE OF CUSTODY")); // best span, not earliest
        Assert.Null(TextMatching.FindPhrase([W("WHOM"), W("RECEIVED"), W("ADDRESS")], "RECEIVED BY"));  // a missing word is not a match
        static OcrWord W(string t) => new(t, 90m, 0, 0, 10, 10, 1, 1, 1, 1);
    }

    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        var swap = degrees is 90 or 270;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return rotated;
    }

    private static SKBitmap Skew(SKBitmap source, float degrees)
    {
        var skewed = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(skewed);
        canvas.Clear(SKColors.White);
        canvas.Translate(source.Width / 2f, source.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return skewed;
    }

    private static SKBitmap Fade(SKBitmap source, float contrast, bool noise)
    {
        var faded = new SKBitmap(source.Width, source.Height);
        using var canvas = new SKCanvas(faded);
        canvas.Clear(SKColors.White);
        // Skia's colour matrix takes its translation column in 0..1 units: black text lands at
        // (1 - contrast) * 0.8 of white, i.e. a faint grey on a slightly grey ground.
        var lift = (1 - contrast) * 0.8f;
        using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix([contrast, 0, 0, 0, lift, 0, contrast, 0, 0, lift, 0, 0, contrast, 0, lift, 0, 0, 0, 1, 0]) };
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default, paint);
        if (noise)
        {
            var rng = new Random(12345);
            using var dot = new SKPaint { Color = new SKColor(120, 120, 120) };
            for (var i = 0; i < source.Width * source.Height / 400; i++)
            {
                canvas.DrawRect(rng.Next(source.Width), rng.Next(source.Height), 1, 1, dot);
            }
        }

        return faded;
    }
}
