using Emc.Domain.Ocr;

namespace Emc.Application.Ocr;

/// <summary>
/// The fallback mapper: every recognized line becomes a field Page[n].Line[k], flagged for
/// verification (none is High-consequence, but a line of an MFR or an outside agency's custody
/// form is still something a person reads before it is used). Always identifies, at the lowest
/// possible score, so it must be registered LAST. A run mapped with it records
/// TemplateIdentified = false.
/// </summary>
public sealed class GenericLineTemplateMapper : IFormTemplateMapper
{
    public const string Id = "generic-lines/1";

    public string TemplateId => Id;

    public decimal IdentificationThreshold => 0m;

    public decimal Identify(IReadOnlyList<RecognizedPage> pages) => 0m;

    public IReadOnlyList<ExtractedFieldCandidate> Map(IReadOnlyList<RecognizedPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var fields = new List<ExtractedFieldCandidate>();
        foreach (var page in pages)
        {
            var lineIndex = 0;
            foreach (var line in page.Result.Lines)
            {
                var words = line.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Left).ToList();
                if (words.Count == 0)
                {
                    continue;
                }

                lineIndex++;
                var left = words.Min(w => w.Left);
                var top = words.Min(w => w.Top);
                var right = words.Max(w => w.Left + w.Width);
                var bottom = words.Max(w => w.Top + w.Height);
                var text = string.Join(' ', words.Select(w => w.Text.Trim()));
                var confidence = Math.Round(words.Min(w => w.Confidence), 2);
                fields.Add(new ExtractedFieldCandidate(
                    OcrFieldCatalog.GenericLine(page.PageNumber, lineIndex), page.PageNumber,
                    text, null, confidence, left, top, right - left, bottom - top));
            }
        }

        return fields;
    }
}
