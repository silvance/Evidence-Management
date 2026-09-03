using System.Globalization;
using System.Text.RegularExpressions;
using Emc.Domain.Ocr;

namespace Emc.Application.Ocr.DaForm4137;

/// <summary>
/// Which face of the form a page is, decided from the printed labels the engine read.
/// AR 195-5 2-3a: the form is two-sided with a vertical flip; 2-3h: a Continuation of
/// Description of Articles page is bond paper headed with that sentence; 2-3i: a continuation of
/// the chain of custody is a NEW DA Form 4137 with "Continuation of Chain of Custody, dated ..."
/// in the middle of its Description of Articles section.
/// </summary>
public enum DaForm4137Face
{
    Unknown = 0,
    Front = 1,
    Back = 2,
    ContinuationOfDescriptionOfArticles = 3,
    ContinuationOfChainOfCustody = 4
}

/// <summary>
/// [DESIGN] OCR-007. The DA Form 4137 mapper. It is LABEL-ANCHORED, not coordinate-fixed: it
/// finds the form's printed block labels in the recognized words and reads each block's value
/// from the words beneath (or beside) its label, bounded by the neighbouring labels. So it does
/// not care about DPI, margins, scanner cropping, or which edition's exact geometry was used,
/// and every value it emits carries the box the engine read it from.
///
/// What it emits, and only this:
///   Header.*            the identification blocks, including the DOCUMENT NUMBER and the case
///                       control / law enforcement report number;
///   Item[n].*           the Description of Articles rows (front and 2-3h continuation pages),
///                       with a serial number or IMEI split out of the description when present;
///   Custody[n].*        the Chain of Custody rows (front, back, 2-3i continuation);
///   Disposition.*       the Final Disposal Action / Authority blocks on the back.
///
/// It never decides that a value is right. A block whose label was found but whose value area
/// holds no words is emitted EMPTY at zero confidence - Low/Unreadable, manual entry - because
/// "the engine saw nothing there" is information a verifier needs, and a guess is not.
/// </summary>
public sealed partial class DaForm4137TemplateMapper : IFormTemplateMapper
{
    public const string Id = "da-form-4137/1";

    public string TemplateId => Id;

    public decimal IdentificationThreshold => 0.5m;

    // Anchor phrases. Alternatives cover the editions' wordings; any one of a group counts.
    private static readonly string[][] IdentityAnchors =
    [
        ["EVIDENCE/PROPERTY CUSTODY DOCUMENT", "EVIDENCE PROPERTY CUSTODY DOCUMENT"],
        ["DA FORM 4137"],
        ["DESCRIPTION OF ARTICLES"],
        ["CHAIN OF CUSTODY"],
        ["RECEIVING ACTIVITY"],
        ["PURPOSE OF CHANGE OF CUSTODY"]
    ];

    private static readonly string[] ContinuationOfDescriptionAnchors = ["CONTINUATION OF DESCRIPTION OF ARTICLES"];
    private static readonly string[] ContinuationOfChainAnchors = ["CONTINUATION OF CHAIN OF CUSTODY"];
    private static readonly string[] BackAnchors = ["FINAL DISPOSAL ACTION", "FINAL DISPOSAL AUTHORITY", "WITNESS TO DESTRUCTION"];

    /// <summary>Header blocks: field key, label alternatives, whether the value sits to the RIGHT of the label on the same line (else below).</summary>
    private static readonly (string Key, string[] Labels)[] HeaderBlocks =
    [
        (OcrFieldCatalog.ReceivingActivity, ["RECEIVING ACTIVITY"]),
        (OcrFieldCatalog.Location, ["LOCATION"]),
        (OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived, ["NAME GRADE AND TITLE OF PERSON FROM WHOM RECEIVED", "PERSON FROM WHOM RECEIVED"]),
        (OcrFieldCatalog.AddressOfPersonFromWhomReceived, ["ADDRESS INCLUDE ZIP CODE", "ADDRESS"]),
        (OcrFieldCatalog.LocationFromWhereObtained, ["LOCATION FROM WHERE OBTAINED"]),
        (OcrFieldCatalog.ReasonObtained, ["REASON OBTAINED"]),
        (OcrFieldCatalog.DateTimeObtained, ["DATE TIME OBTAINED", "DATE/TIME OBTAINED"]),
        (OcrFieldCatalog.CaseControlNumber, ["CASE CONTROL NUMBER", "LAW ENFORCEMENT REPORT NUMBER", "CID SEQUENCE NUMBER", "MPR CID SEQUENCE NUMBER", "CRD REPORT CID ROI NUMBER"]),
        (OcrFieldCatalog.DocumentNumber, ["DOCUMENT NUMBER"])
    ];

    /// <summary>Labels printed inside a block's value area (tick boxes, signature lines): never a row boundary.</summary>
    private static readonly HashSet<string> InlineLabels = new(StringComparer.Ordinal) { "OWNER", "OTHER", "SIGNATURE", "NAME GRADE OR TITLE" };

    private static readonly string[] AllLabelPhrases =
        HeaderBlocks.SelectMany(b => b.Labels)
            .Concat(["ITEM NO", "QUANTITY", "DESCRIPTION OF ARTICLES", "CHAIN OF CUSTODY", "DATE", "RELEASED BY", "RECEIVED BY",
                     "PURPOSE OF CHANGE OF CUSTODY", "SIGNATURE", "NAME GRADE OR TITLE", "FINAL DISPOSAL ACTION", "FINAL DISPOSAL AUTHORITY",
                     "WITNESS TO DESTRUCTION OF EVIDENCE", "OWNER", "OTHER", "EVIDENCE PROPERTY CUSTODY DOCUMENT", "DA FORM 4137",
                     "CONTINUATION OF DESCRIPTION OF ARTICLES", "CONTINUATION OF CHAIN OF CUSTODY", "LAST ITEM"])
            .Distinct()
            .ToArray();

    public decimal Identify(IReadOnlyList<RecognizedPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) return 0m;

        // The first page decides; a package that starts with a continuation page still carries
        // the form's vocabulary, so the anchors are searched on it.
        var lines = Lines(pages[0]);
        var found = IdentityAnchors.Count(group => group.Any(phrase => lines.Any(l => TextMatching.FindPhrase(l.Words, phrase) is not null)));
        return Math.Round((decimal)found / IdentityAnchors.Length, 2);
    }

    public static DaForm4137Face Classify(RecognizedPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var lines = Lines(page);
        bool Has(string[] phrases) => phrases.Any(p => lines.Any(l => TextMatching.FindPhrase(l.Words, p) is not null));

        if (Has(ContinuationOfDescriptionAnchors)) return DaForm4137Face.ContinuationOfDescriptionOfArticles;
        if (Has(ContinuationOfChainAnchors)) return DaForm4137Face.ContinuationOfChainOfCustody;
        if (Has(["RECEIVING ACTIVITY"]) && (Has(["DESCRIPTION OF ARTICLES"]) || Has(["CHAIN OF CUSTODY"]))) return DaForm4137Face.Front;
        if (Has(BackAnchors) || (Has(["CHAIN OF CUSTODY"]) && !Has(["RECEIVING ACTIVITY"]))) return DaForm4137Face.Back;
        return DaForm4137Face.Unknown;
    }

    public IReadOnlyList<ExtractedFieldCandidate> Map(IReadOnlyList<RecognizedPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var fields = new List<ExtractedFieldCandidate>();
        var itemIndex = 0;
        var custodyIndex = 0;

        foreach (var page in pages)
        {
            var face = Classify(page);
            var lines = Lines(page);
            var labels = FindLabels(lines);
            var pageNumber = page.PageNumber;

            switch (face)
            {
                case DaForm4137Face.Front:
                case DaForm4137Face.ContinuationOfChainOfCustody:
                    fields.AddRange(HeaderFields(pageNumber, lines, labels));
                    if (face == DaForm4137Face.Front)
                    {
                        fields.AddRange(ItemRows(pageNumber, lines, labels, ref itemIndex));
                    }
                    fields.AddRange(CustodyRows(pageNumber, lines, labels, ref custodyIndex));
                    break;

                case DaForm4137Face.ContinuationOfDescriptionOfArticles:
                    // 2-3h: case number, receiving activity, location, person from whom received at the top; then items.
                    fields.AddRange(HeaderFields(pageNumber, lines, labels, onlyKeys:
                        [OcrFieldCatalog.CaseControlNumber, OcrFieldCatalog.ReceivingActivity, OcrFieldCatalog.Location, OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived]));
                    fields.AddRange(ItemRows(pageNumber, lines, labels, ref itemIndex));
                    break;

                case DaForm4137Face.Back:
                    fields.AddRange(HeaderFields(pageNumber, lines, labels, onlyKeys: [OcrFieldCatalog.DocumentNumber]));
                    fields.AddRange(CustodyRows(pageNumber, lines, labels, ref custodyIndex));
                    fields.AddRange(DispositionFields(pageNumber, lines, labels));
                    break;

                default:
                    // An unclassifiable page in a DA 4137 package (an attached MFR, an outside
                    // agency's custody form): generic lines, so nothing on it is lost.
                    var generic = new GenericLineTemplateMapper().Map([page]);
                    fields.AddRange(generic);
                    break;
            }
        }

        return fields;
    }

    /// <summary>Values that appear on more than one page and disagree - a document number that differs between front and back, say. Reconciliation shows these first.</summary>
    public static IReadOnlyList<(string FieldKey, IReadOnlyList<ExtractedFieldCandidate> Values)> Conflicts(IEnumerable<ExtractedFieldCandidate> fields)
        => fields.Where(f => f.RawText.Length > 0)
            .GroupBy(f => f.FieldKey, StringComparer.Ordinal)
            .Where(g => g.Select(f => TextMatching.Normalize(f.NormalizedCandidate ?? f.RawText)).Distinct().Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<ExtractedFieldCandidate>)g.ToList()))
            .ToList();

    // ---- geometry -------------------------------------------------------------------------

    internal sealed record Line(IReadOnlyList<OcrWord> Words, int Left, int Top, int Right, int Bottom)
    {
        public int Height => Bottom - Top;
        public string Text => string.Join(' ', Words.Select(w => w.Text));
    }

    internal sealed record Label(string Phrase, Line Line, IReadOnlyList<OcrWord> Words, int Left, int Top, int Right, int Bottom);

    internal static List<Line> Lines(RecognizedPage page)
        => page.Result.Lines
            .Select(g => g.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Left).ToList())
            .Where(ws => ws.Count > 0)
            .Select(ws => new Line(ws, ws.Min(w => w.Left), ws.Min(w => w.Top), ws.Max(w => w.Left + w.Width), ws.Max(w => w.Top + w.Height)))
            .OrderBy(l => l.Top).ThenBy(l => l.Left)
            .ToList();

    internal static List<Label> FindLabels(List<Line> lines)
    {
        var labels = new List<Label>();
        foreach (var line in lines)
        {
            // Longest phrases first so "LOCATION FROM WHERE OBTAINED" is not claimed by "LOCATION".
            var claimed = new bool[line.Words.Count];
            foreach (var phrase in AllLabelPhrases.OrderByDescending(p => p.Length))
            {
                var hit = TextMatching.FindPhrase(line.Words, phrase);
                if (hit is null) continue;
                var (s, e) = hit.Value;
                if (Enumerable.Range(s, e - s + 1).Any(i => claimed[i])) continue;

                // OWNER / OTHER are tick-box labels only when a tick mark precedes them ("[X]",
                // "[]", "{}"); the same words inside a sentence ("RETURNED TO OWNER") are values.
                if (phrase is "OWNER" or "OTHER" && s > 0 && TextMatching.Normalize(line.Words[s - 1].Text).Length > 1) continue;
                for (var i = s; i <= e; i++) claimed[i] = true;
                var ws = line.Words.Skip(s).Take(e - s + 1).ToList();
                labels.Add(new Label(phrase, line, ws, ws.Min(w => w.Left), ws.Min(w => w.Top), ws.Max(w => w.Left + w.Width), ws.Max(w => w.Top + w.Height)));
            }
        }

        return labels;
    }

    private static Label? FindLabel(List<Label> labels, string[] alternatives)
        => alternatives.Select(a => labels.FirstOrDefault(l => l.Phrase == a)).FirstOrDefault(l => l is not null);

    /// <summary>
    /// The words in a block's value area: same line to the right of the label (up to the next
    /// label on that line), plus the lines below it until the next label row, restricted to the
    /// label's column (from its left edge to the next label to its right, or the page edge).
    /// Words that are themselves part of a label are excluded.
    /// </summary>
    private static List<OcrWord> ValueWords(Label label, List<Line> lines, List<Label> labels, int pageWidth)
    {
        var labelWordSet = new HashSet<OcrWord>(labels.SelectMany(l => l.Words), ReferenceEqualityComparer.Instance);
        var rightNeighbour = labels.Where(l => l != label && l.Left > label.Left && Math.Abs(l.Top - label.Top) < label.Line.Height * 1.5)
            .OrderBy(l => l.Left).FirstOrDefault();
        var columnRight = rightNeighbour?.Left - 2 ?? pageWidth;

        // The value area ends at the next label ROW: form rows span the page, so the nearest
        // label below this one - in any column - marks where the next row's labels begin. Tick
        // and signature-line labels sit INSIDE a value area and do not end it.
        var below = labels.Where(l => l != label && l.Top > label.Bottom + 2 && !InlineLabels.Contains(l.Phrase))
            .OrderBy(l => l.Top).FirstOrDefault();
        var bottom = below?.Top - 1 ?? int.MaxValue;

        var words = new List<OcrWord>();
        foreach (var line in lines)
        {
            if (line.Bottom < label.Top) continue;
            if (line.Top > bottom) break;
            foreach (var w in line.Words)
            {
                if (labelWordSet.Contains(w)) continue;
                var cx = w.Left + w.Width / 2;
                if (cx < label.Left - 4 || cx > columnRight) continue;
                if (w.Top + w.Height / 2 < label.Top) continue;
                words.Add(w);
            }
        }

        // Reading order: the engine's own lines, ordered by their top edge; left to right within.
        var lineTop = words.GroupBy(w => (w.BlockIndex, w.ParagraphIndex, w.LineIndex)).ToDictionary(g => g.Key, g => g.Min(w => w.Top));
        return words.OrderBy(w => lineTop[(w.BlockIndex, w.ParagraphIndex, w.LineIndex)]).ThenBy(w => w.Left).ToList();
    }

    private static ExtractedFieldCandidate Candidate(string key, int page, IReadOnlyList<OcrWord> words, Label? label, string? normalized = null)
    {
        if (words.Count == 0)
        {
            // Label found, nothing beneath it: EMPTY at zero confidence, so the verifier enters it.
            return new ExtractedFieldCandidate(key, page, string.Empty, null, 0m,
                label?.Left ?? 0, label?.Bottom ?? 0, label is null ? 0 : Math.Max(1, label.Right - label.Left), label is null ? 0 : Math.Max(1, label.Bottom - label.Top));
        }

        var text = string.Join(' ', words.Select(w => w.Text));
        var left = words.Min(w => w.Left); var top = words.Min(w => w.Top);
        var right = words.Max(w => w.Left + w.Width); var bottom = words.Max(w => w.Top + w.Height);
        return new ExtractedFieldCandidate(key, page, text, normalized ?? Normalizers.For(key, text), Math.Round(words.Min(w => w.Confidence), 2), left, top, right - left, bottom - top);
    }

    private static IEnumerable<ExtractedFieldCandidate> HeaderFields(int page, List<Line> lines, List<Label> labels, string[]? onlyKeys = null)
    {
        var pageWidth = lines.Count == 0 ? 0 : lines.Max(l => l.Right) + 50;
        foreach (var (key, alternatives) in HeaderBlocks)
        {
            if (onlyKeys is not null && !onlyKeys.Contains(key, StringComparer.Ordinal)) continue;
            var label = FindLabel(labels, alternatives);
            if (label is null) continue;
            var words = ValueWords(label, lines, labels, pageWidth);

            if (key == OcrFieldCatalog.NameGradeTitleOfPersonFromWhomReceived)
            {
                // OWNER / OTHER tick labels sit inside this block; the name is what is left.
                words = words.Where(w => !TextMatching.WordMatches("OWNER", w.Text) && !TextMatching.WordMatches("OTHER", w.Text) && w.Text.Trim() is not ("X" or "[X]" or "☒" or "☐")).ToList();
            }

            yield return Candidate(key, page, words, label);
        }
    }

    private static IEnumerable<ExtractedFieldCandidate> ItemRows(int page, List<Line> lines, List<Label> labels, ref int itemIndex)
    {
        var results = new List<ExtractedFieldCandidate>();
        var index = itemIndex;
        // The table's header row: the DESCRIPTION OF ARTICLES label that shares a row with an
        // ITEM NO label, else the lowest one (the 2-3h sentence at the top of a continuation page
        // is claimed by its own, longer phrase, but a mixed-case title can still match here).
        var candidates = labels.Where(l => l.Phrase == "DESCRIPTION OF ARTICLES").ToList();
        var header = candidates.FirstOrDefault(h => labels.Any(l => l.Phrase == "ITEM NO" && Math.Abs(l.Top - h.Top) < h.Line.Height * 2))
                     ?? candidates.OrderByDescending(h => h.Top).FirstOrDefault();
        var itemNo = labels.Where(l => l.Phrase == "ITEM NO" && header is not null && Math.Abs(l.Top - header.Top) < header.Line.Height * 2).OrderBy(l => l.Top).FirstOrDefault();
        var quantity = labels.Where(l => l.Phrase == "QUANTITY" && header is not null && Math.Abs(l.Top - header.Top) < header.Line.Height * 2).FirstOrDefault();
        if (header is null) return results;

        var stop = labels.Where(l => l.Top > header.Bottom && l.Phrase is "CHAIN OF CUSTODY" or "CONTINUATION OF CHAIN OF CUSTODY").OrderBy(l => l.Top).FirstOrDefault()?.Top ?? int.MaxValue;
        var quantityLeft = quantity?.Left ?? (itemNo is null ? -1 : itemNo.Right + 10);
        var descriptionLeft = header.Left;

        foreach (var line in lines.Where(l => l.Top > header.Bottom && l.Top < stop))
        {
            if (TextMatching.FindPhrase(line.Words, "LAST ITEM") is not null) break;
            if (labels.Any(l => l.Line == line)) continue;

            var numberWords = line.Words.Where(w => w.Left + w.Width / 2 < quantityLeft).ToList();
            var quantityWords = line.Words.Where(w => w.Left + w.Width / 2 >= quantityLeft && w.Left + w.Width / 2 < descriptionLeft - 4).ToList();
            var descriptionWords = line.Words.Where(w => w.Left + w.Width / 2 >= descriptionLeft - 4).ToList();

            var isNewItem = numberWords.Count > 0 && Normalizers.ItemNumber(string.Join(' ', numberWords.Select(w => w.Text))) is not null;
            if (isNewItem)
            {
                index++;
                results.Add(Candidate($"Item[{index}].{OcrFieldCatalog.ItemNumberField}", page, numberWords, null));
                if (quantity is not null || quantityWords.Count > 0)
                {
                    results.Add(Candidate($"Item[{index}].{OcrFieldCatalog.ItemQuantityField}", page, quantityWords, quantity));
                }

                results.Add(Candidate($"Item[{index}].{OcrFieldCatalog.ItemDescriptionField}", page, descriptionWords, header));
            }
            else if (index > 0 && descriptionWords.Count > 0)
            {
                // A wrapped description line: append to the current item's description.
                var idx = results.FindLastIndex(r => r.FieldKey == $"Item[{index}].{OcrFieldCatalog.ItemDescriptionField}");
                if (idx >= 0)
                {
                    var prev = results[idx];
                    var text = prev.RawText.Length == 0 ? string.Join(' ', descriptionWords.Select(w => w.Text)) : prev.RawText + " " + string.Join(' ', descriptionWords.Select(w => w.Text));
                    var right = Math.Max(prev.Left + prev.Width, descriptionWords.Max(w => w.Left + w.Width));
                    var bottom = Math.Max(prev.Top + prev.Height, descriptionWords.Max(w => w.Top + w.Height));
                    var left = Math.Min(prev.Left, descriptionWords.Min(w => w.Left));
                    var top = Math.Min(prev.Top, descriptionWords.Min(w => w.Top));
                    results[idx] = prev with { RawText = text, Confidence = Math.Min(prev.Confidence, descriptionWords.Min(w => w.Confidence)), Left = left, Top = top, Width = right - left, Height = bottom - top };
                }
            }
        }

        // Serial numbers and device identifiers split out of descriptions (2-3d: "If serial
        // numbers are available ... they will be recorded").
        foreach (var d in results.Where(r => r.FieldKey.EndsWith("." + OcrFieldCatalog.ItemDescriptionField, StringComparison.Ordinal)).ToList())
        {
            var prefix = d.FieldKey[..d.FieldKey.IndexOf('.', StringComparison.Ordinal)];
            var serial = Normalizers.SerialFromDescription(d.RawText);
            if (serial is not null)
            {
                results.Add(new ExtractedFieldCandidate($"{prefix}.{OcrFieldCatalog.ItemSerialNumberField}", d.PageNumber, serial, Normalizers.Identifier(serial), d.Confidence, d.Left, d.Top, d.Width, d.Height));
            }

            var imei = Normalizers.ImeiFromDescription(d.RawText);
            if (imei is not null)
            {
                results.Add(new ExtractedFieldCandidate($"{prefix}.{OcrFieldCatalog.ItemUniqueDeviceIdentifierField}", d.PageNumber, imei, Normalizers.Identifier(imei), d.Confidence, d.Left, d.Top, d.Width, d.Height));
            }
        }

        itemIndex = index;
        return results;
    }

    private static IEnumerable<ExtractedFieldCandidate> CustodyRows(int page, List<Line> lines, List<Label> labels, ref int custodyIndex)
    {
        var results = new List<ExtractedFieldCandidate>();
        var index = custodyIndex;
        var released = labels.FirstOrDefault(l => l.Phrase == "RELEASED BY");
        var received = labels.FirstOrDefault(l => l.Phrase == "RECEIVED BY");
        var purpose = labels.FirstOrDefault(l => l.Phrase == "PURPOSE OF CHANGE OF CUSTODY");
        if (released is null || received is null) return results;

        var headerTop = Math.Min(released.Top, received.Top);
        var headerBottom = Math.Max(released.Bottom, received.Bottom);
        var date = labels.Where(l => l.Phrase == "DATE" && Math.Abs(l.Top - headerTop) < released.Line.Height * 2).FirstOrDefault();
        var itemNo = labels.Where(l => l.Phrase == "ITEM NO" && Math.Abs(l.Top - headerTop) < released.Line.Height * 2).FirstOrDefault();

        var stop = labels.Where(l => l.Top > headerBottom && l.Phrase is "FINAL DISPOSAL ACTION" or "FINAL DISPOSAL AUTHORITY" or "WITNESS TO DESTRUCTION OF EVIDENCE" or "DOCUMENT NUMBER")
            .OrderBy(l => l.Top).FirstOrDefault()?.Top ?? int.MaxValue;

        // Column boundaries from the header labels' left edges.
        var dateLeft = date?.Left ?? released.Left - 1;
        var releasedLeft = released.Left;
        var receivedLeft = received.Left;
        var purposeLeft = purpose?.Left ?? int.MaxValue;

        // Rows: each custody entry spans two printed lines (signature / name, grade or title);
        // a row begins on a line whose first column holds an item number or a date.
        var rowLines = new List<Line>();
        foreach (var line in lines.Where(l => l.Top > headerBottom && l.Top < stop))
        {
            if (labels.Any(l => l.Line == line && l.Phrase is "SIGNATURE" or "NAME GRADE OR TITLE") && line.Words.Count <= 5) continue;
            var firstCol = line.Words.Where(w => w.Left + w.Width / 2 < dateLeft).ToList();
            var dateCol = line.Words.Where(w => w.Left + w.Width / 2 >= dateLeft && w.Left + w.Width / 2 < releasedLeft).ToList();
            var startsRow = (firstCol.Count > 0 && Normalizers.ItemNumberList(string.Join(' ', firstCol.Select(w => w.Text))) is not null)
                            || (dateCol.Count > 0 && Normalizers.Date(string.Join(' ', dateCol.Select(w => w.Text))) is not null);
            if (startsRow || rowLines.Count == 0)
            {
                if (rowLines.Count > 0) Emit(rowLines);
                rowLines = [line];
            }
            else
            {
                rowLines.Add(line);
            }
        }

        if (rowLines.Count > 0) Emit(rowLines);
        custodyIndex = index;
        return results;

        void Emit(List<Line> group)
        {
            var words = group.SelectMany(l => l.Words).Where(w => !labels.Any(l => l.Line.Words.Contains(w) && (l.Phrase is "SIGNATURE" or "NAME GRADE OR TITLE"))).ToList();
            if (words.Count == 0) return;
            index++;
            var k = index;
            var item = words.Where(w => w.Left + w.Width / 2 < dateLeft).ToList();
            var dateWords = words.Where(w => w.Left + w.Width / 2 >= dateLeft && w.Left + w.Width / 2 < releasedLeft).ToList();
            var releasedWords = words.Where(w => w.Left + w.Width / 2 >= releasedLeft && w.Left + w.Width / 2 < receivedLeft).ToList();
            var receivedWords = words.Where(w => w.Left + w.Width / 2 >= receivedLeft && w.Left + w.Width / 2 < purposeLeft).ToList();
            var purposeWords = words.Where(w => w.Left + w.Width / 2 >= purposeLeft).ToList();

            results.Add(Candidate($"Custody[{k}].{OcrFieldCatalog.CustodyItemNumberField}", page, item, itemNo));
            results.Add(Candidate($"Custody[{k}].{OcrFieldCatalog.CustodyDateField}", page, dateWords, date));
            results.Add(Candidate($"Custody[{k}].{OcrFieldCatalog.CustodyReleasedByNameField}", page, releasedWords, released));
            results.Add(Candidate($"Custody[{k}].{OcrFieldCatalog.CustodyReceivedByNameField}", page, receivedWords, received));
            results.Add(Candidate($"Custody[{k}].{OcrFieldCatalog.CustodyPurposeField}", page, purposeWords, purpose));
        }
    }

    private static IEnumerable<ExtractedFieldCandidate> DispositionFields(int page, List<Line> lines, List<Label> labels)
    {
        var pageWidth = lines.Count == 0 ? 0 : lines.Max(l => l.Right) + 50;
        var action = FindLabel(labels, ["FINAL DISPOSAL ACTION"]);
        if (action is not null)
        {
            yield return Candidate(OcrFieldCatalog.DispositionAction, page, ValueWords(action, lines, labels, pageWidth), action);
        }

        var authority = FindLabel(labels, ["FINAL DISPOSAL AUTHORITY"]);
        if (authority is not null)
        {
            yield return Candidate(OcrFieldCatalog.DispositionAuthority, page, ValueWords(authority, lines, labels, pageWidth), authority);
        }
    }

    /// <summary>Candidates in the field's expected shape. A candidate is an offer, never a decision.</summary>
    internal static partial class Normalizers
    {
        [GeneratedRegex(@"(?<!\d)(\d{1,4})\s*[-–—]\s*(\d{2})(?!\d)")]
        private static partial Regex DocumentNumberPattern();

        [GeneratedRegex(@"^\s*(\d{1,3})\s*$")]
        private static partial Regex ItemNumberPattern();

        [GeneratedRegex(@"^\s*\d{1,3}(\s*[-,&]\s*\d{1,3})*\s*$")]
        private static partial Regex ItemNumberListPattern();

        [GeneratedRegex(@"(?<!\d)(\d{1,2})\s+([A-Za-z]{3})\s+(\d{2}|\d{4})(?!\d)")]
        private static partial Regex DatePattern();

        [GeneratedRegex(@"(?:S/N|SN|SERIAL(?:\s+NO\.?|\s+NUMBER)?)[:#\s]*([A-Z0-9][A-Z0-9\-]{3,})", RegexOptions.IgnoreCase)]
        private static partial Regex SerialPattern();

        [GeneratedRegex(@"IMEI[:#\s]*(\d[\d ]{13,16}\d)", RegexOptions.IgnoreCase)]
        private static partial Regex ImeiPattern();

        private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

        public static string? For(string key, string text)
        {
            var field = OcrFieldCatalog.FieldName(key);
            return field switch
            {
                "DocumentNumber" => DocumentNumber(text),
                "ItemNumber" => OcrFieldCatalog.Section(key) == "Custody" ? ItemNumberList(text) : ItemNumber(text),
                "Date" => Date(text),
                "DateTimeObtained" => Date(text) is { } d ? d + TimeSuffix(text) : null,
                "CaseControlNumber" => CaseControlNumber(text),
                "SerialNumber" or "UniqueDeviceIdentifier" => Identifier(text),
                _ => null
            };
        }

        public static string? DocumentNumber(string text)
        {
            // Common confusions in a digits-only field: O/o -> 0, I/l -> 1, S -> 5, B -> 8.
            var cleaned = text.Replace('O', '0').Replace('o', '0').Replace('I', '1').Replace('l', '1').Replace('S', '5').Replace('B', '8');
            var m = DocumentNumberPattern().Match(cleaned);
            // AR 195-5 2-4f(1) shows the regulatory form: three-digit sequence, two-digit year ("001 - 18").
            return m.Success ? $"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture):000}-{m.Groups[2].Value}" : null;
        }

        public static string? ItemNumber(string text)
        {
            var m = ItemNumberPattern().Match(text.Replace('O', '0').Replace('l', '1').Replace('I', '1'));
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null;
        }

        public static string? ItemNumberList(string text)
        {
            var cleaned = text.Replace('O', '0').Replace('l', '1').Replace('I', '1');
            return ItemNumberListPattern().IsMatch(cleaned) ? Regex.Replace(cleaned, @"\s+", string.Empty) : null;
        }

        public static string? Date(string text)
        {
            var m = DatePattern().Match(text);
            if (!m.Success) return null;
            var month = m.Groups[2].Value.ToUpperInvariant();
            if (!Months.Contains(month)) return null;
            var year = m.Groups[3].Value;
            if (year.Length == 4) year = year[2..];
            return $"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture):00} {month} {year}";
        }

        private static string TimeSuffix(string text)
        {
            var m = Regex.Match(text, @"(?<!\d)([01]\d|2[0-3]):?([0-5]\d)(?!\d)");
            return m.Success ? $" {m.Groups[1].Value}{m.Groups[2].Value}" : string.Empty;
        }

        public static string? CaseControlNumber(string text)
        {
            var compact = TextMatching.Normalize(text);
            return compact.Length is >= 6 and <= 40 ? text.Trim().ToUpperInvariant().Replace(" ", string.Empty) : null;
        }

        /// <summary>
        /// Letters and digits only, with the two confusions an engine makes inside a digit run
        /// resolved: a letter O between digits is offered as 0, a letter I or l between digits as 1.
        /// A candidate, verified by a person before it is anything (OCR-003).
        /// </summary>
        public static string Identifier(string text)
        {
            var chars = TextMatching.Normalize(text).ToCharArray();

            // A letter O immediately before a digit (or before another such O) is offered as 0:
            // "TESTSERIALOO0002" -> "TESTSERIAL000002". Only O; an L or I before a digit run is
            // usually a real letter ("SERIAL0001"), and is offered as 1 only BETWEEN digits.
            for (var i = chars.Length - 2; i >= 0; i--)
            {
                if (chars[i] == 'O' && char.IsDigit(chars[i + 1]))
                {
                    chars[i] = '0';
                }
            }

            for (var i = 1; i < chars.Length - 1; i++)
            {
                if (!char.IsDigit(chars[i - 1]) || !char.IsDigit(chars[i + 1])) continue;
                chars[i] = chars[i] switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => chars[i] };
            }

            return new string(chars);
        }

        public static string? SerialFromDescription(string description)
        {
            var m = SerialPattern().Match(description);
            return m.Success ? m.Groups[1].Value.TrimEnd('.', ',', ';') : null;
        }

        public static string? ImeiFromDescription(string description)
        {
            var m = ImeiPattern().Match(description);
            return m.Success ? m.Groups[1].Value.Replace(" ", string.Empty) : null;
        }
    }
}
