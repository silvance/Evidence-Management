using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Emc.Application.Tests;

/// <summary>
/// A SYNTHETIC DA Form 4137-shaped page set for mapping tests: the form's printed block labels
/// (public, from the form itself) laid out in this generator's own geometry, filled with values
/// that are unmistakably fictitious (TEST case numbers, TEST names, all-zero identifiers). It is
/// not a reproduction of the form, and no real form ever appears in this repository. The mapper
/// under test is label-anchored, so it must read this layout as readily as any edition's.
/// </summary>
public static class SyntheticDaForm4137
{
    public sealed record Header(
        string ReceivingActivity = "TEST EVIDENCE ROOM, 000TH MI TEST BN",
        string Location = "FORT TEST, TS 00000",
        string ReceivedFrom = "SMITH, TEST A., SGT",
        string Address = "000 TEST STREET, TEST CITY TS 00000",
        string LocationObtained = "TEST BARRACKS ROOM 000",
        string ReasonObtained = "SEIZED AS EVIDENCE - TEST",
        string DateTimeObtained = "03 SEP 26 0915",
        string CaseControlNumber = "TEST-CI-2026-0001",
        string DocumentNumber = "007-26");

    public sealed record Item(int Number, string Quantity, string Description);

    public sealed record Custody(string ItemNumbers, string Date, string ReleasedBy, string ReceivedBy, string Purpose);

    public static readonly Item[] DefaultItems =
    [
        new(1, "1", "ONE TEST MOBILE TELEPHONE, BLACK, IMEI 000000000000001"),
        new(2, "1", "ONE TEST LAPTOP COMPUTER, GRAY, S/N TESTSERIAL000002, POWER CORD ATTACHED"),
        new(3, "APPROX 100", "TEST TABLETS, WHITE, IN A SEALED TEST BAG")
    ];

    public static readonly Custody[] DefaultCustody =
    [
        new("1-3", "03 SEP 26", "SMITH, TEST A. SGT", "JONES, TEST B. SA", "SEIZED AS EVIDENCE"),
        new("1-3", "04 SEP 26", "JONES, TEST B. SA", "BAKER, TEST C. SA", "RELEASED TO EVIDENCE CUSTODIAN")
    ];

    public sealed class Options
    {
        public Header Header { get; init; } = new();
        public Item[] Items { get; init; } = DefaultItems;
        public Custody[] FrontCustody { get; init; } = DefaultCustody;
        public Custody[] BackCustody { get; init; } = [];
        public bool IncludeBack { get; init; } = true;
        public string? BackDocumentNumber { get; init; }
        public string? FinalDisposalAction { get; init; }
        public string? FinalDisposalAuthority { get; init; }

        /// <summary>Items that overflow onto a 2-3h "Continuation of Description of Articles" page.</summary>
        public Item[] ContinuationItems { get; init; } = [];

        /// <summary>A 2-3i continuation: a new form with the chain continuing.</summary>
        public Custody[] ChainContinuation { get; init; } = [];

        /// <summary>Blacks out the named header block's value so nothing is readable there.</summary>
        public string? UnreadableBlock { get; init; }
    }

    public static byte[] Build(Options? options = null)
    {
        options ??= new Options();
        using var document = new PdfDocument();
        DrawFront(document.AddPage(), options, options.Header.DocumentNumber, options.Items, options.FrontCustody, chainContinuationOf: null);
        if (options.IncludeBack)
        {
            DrawBack(document.AddPage(), options.BackDocumentNumber ?? options.Header.DocumentNumber, options.BackCustody, options.FinalDisposalAction, options.FinalDisposalAuthority);
        }

        if (options.ContinuationItems.Length > 0)
        {
            DrawContinuationOfDescription(document.AddPage(), options.Header, options.ContinuationItems);
        }

        if (options.ChainContinuation.Length > 0)
        {
            DrawFront(document.AddPage(), options, options.Header.DocumentNumber, [], options.ChainContinuation, chainContinuationOf: options.FrontCustody[^1].Date);
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    private static readonly XFont Label;
    private static readonly XFont Value;
    private static readonly XFont Title;
    private static readonly XPen Rule = new(XColors.Black, 0.6);

    static SyntheticDaForm4137()
    {
        SyntheticPdf.EnsureFontResolver();
        Label = new XFont("Arial", 8);
        Value = new XFont("Arial", 10);
        Title = new XFont("Arial", 12, XFontStyleEx.Bold);
    }

    private static void DrawFront(PdfPage page, Options options, string documentNumber, Item[] items, Custody[] custody, string? chainContinuationOf)
    {
        page.Width = XUnit.FromInch(8.5);
        page.Height = XUnit.FromInch(11);
        using var g = XGraphics.FromPdfPage(page);
        var h = options.Header;

        g.DrawString("EVIDENCE/PROPERTY CUSTODY DOCUMENT", Title, XBrushes.Black, new XPoint(150, 40));
        g.DrawString("For use of this form, see AR 195-5 - SYNTHETIC TEST FIXTURE, NOT A REAL FORM", Label, XBrushes.Black, new XPoint(150, 52));

        // Top strip: case number and document number.
        Block(g, 36, 60, 300, 34, "CASE CONTROL NUMBER", h.CaseControlNumber, options.UnreadableBlock == "CaseControlNumber");
        Block(g, 336, 60, 240, 34, "DOCUMENT NUMBER", documentNumber, options.UnreadableBlock == "DocumentNumber");

        Block(g, 36, 94, 300, 34, "RECEIVING ACTIVITY", h.ReceivingActivity, options.UnreadableBlock == "ReceivingActivity");
        Block(g, 336, 94, 240, 34, "LOCATION", h.Location, options.UnreadableBlock == "Location");
        Block(g, 36, 128, 300, 34, "NAME, GRADE AND TITLE OF PERSON FROM WHOM RECEIVED", h.ReceivedFrom, options.UnreadableBlock == "ReceivedFrom");
        g.DrawString("[ ] OWNER   [X] OTHER", Label, XBrushes.Black, new XPoint(230, 150));
        Block(g, 336, 128, 240, 34, "ADDRESS (Include Zip Code)", h.Address, false);
        Block(g, 36, 162, 300, 34, "LOCATION FROM WHERE OBTAINED", h.LocationObtained, false);
        Block(g, 336, 162, 140, 34, "REASON OBTAINED", h.ReasonObtained, false);
        Block(g, 476, 162, 100, 34, "DATE/TIME OBTAINED", h.DateTimeObtained, options.UnreadableBlock == "DateTimeObtained");

        // Items table.
        var y = 200.0;
        g.DrawRectangle(Rule, 36, y, 540, 26);
        g.DrawString("ITEM NO.", Label, XBrushes.Black, new XPoint(40, y + 17));
        g.DrawString("QUANTITY", Label, XBrushes.Black, new XPoint(90, y + 17));
        g.DrawString("DESCRIPTION OF ARTICLES (Include model, serial number, condition and unusual marks or scratches)", Label, XBrushes.Black, new XPoint(160, y + 17));
        y += 26;
        if (chainContinuationOf is not null)
        {
            y += 40;
            g.DrawString($"Continuation of Chain of Custody, dated {chainContinuationOf}", Value, XBrushes.Black, new XPoint(200, y));
            y += 30;
        }

        foreach (var item in items)
        {
            y += 16;
            g.DrawString(item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Value, XBrushes.Black, new XPoint(44, y));
            g.DrawString(item.Quantity, Value, XBrushes.Black, new XPoint(92, y));
            g.DrawString(item.Description, Value, XBrushes.Black, new XPoint(162, y));
        }

        if (items.Length > 0 && options.ContinuationItems.Length == 0)
        {
            y += 16;
            LastItem(g, y);
        }

        // Chain of custody.
        y = 470;
        g.DrawString("CHAIN OF CUSTODY", Title, XBrushes.Black, new XPoint(250, y));
        y += 10;
        g.DrawRectangle(Rule, 36, y, 540, 26);
        g.DrawString("ITEM NO.", Label, XBrushes.Black, new XPoint(40, y + 17));
        g.DrawString("DATE", Label, XBrushes.Black, new XPoint(90, y + 17));
        g.DrawString("RELEASED BY", Label, XBrushes.Black, new XPoint(160, y + 17));
        g.DrawString("RECEIVED BY", Label, XBrushes.Black, new XPoint(300, y + 17));
        g.DrawString("PURPOSE OF CHANGE OF CUSTODY", Label, XBrushes.Black, new XPoint(440, y + 17));
        y += 26;
        CustodyRows(g, ref y, custody);
    }

    private static void DrawBack(PdfPage page, string documentNumber, Custody[] custody, string? action, string? authority)
    {
        page.Width = XUnit.FromInch(8.5);
        page.Height = XUnit.FromInch(11);
        using var g = XGraphics.FromPdfPage(page);
        g.DrawString("CHAIN OF CUSTODY (Continued)", Title, XBrushes.Black, new XPoint(220, 40));
        var y = 50.0;
        g.DrawRectangle(Rule, 36, y, 540, 26);
        g.DrawString("ITEM NO.", Label, XBrushes.Black, new XPoint(40, y + 17));
        g.DrawString("DATE", Label, XBrushes.Black, new XPoint(90, y + 17));
        g.DrawString("RELEASED BY", Label, XBrushes.Black, new XPoint(160, y + 17));
        g.DrawString("RECEIVED BY", Label, XBrushes.Black, new XPoint(300, y + 17));
        g.DrawString("PURPOSE OF CHANGE OF CUSTODY", Label, XBrushes.Black, new XPoint(440, y + 17));
        y += 26;
        CustodyRows(g, ref y, custody);

        Block(g, 36, 560, 540, 60, "FINAL DISPOSAL ACTION", action ?? string.Empty, false);
        Block(g, 36, 620, 540, 60, "FINAL DISPOSAL AUTHORITY", authority ?? string.Empty, false);
        Block(g, 36, 680, 300, 40, "WITNESS TO DESTRUCTION OF EVIDENCE", string.Empty, false);
        Block(g, 336, 680, 240, 40, "DOCUMENT NUMBER", documentNumber, false);
        g.DrawString("DA FORM 4137 - SYNTHETIC TEST FIXTURE", Label, XBrushes.Black, new XPoint(36, 760));
    }

    private static void DrawContinuationOfDescription(PdfPage page, Header h, Item[] items)
    {
        // AR 195-5 2-3h: bond paper; the sentence at the top; case number, receiving activity,
        // location and person from whom received as on the original; LAST ITEM after the last.
        page.Width = XUnit.FromInch(8.5);
        page.Height = XUnit.FromInch(11);
        using var g = XGraphics.FromPdfPage(page);
        g.DrawString("Continuation of Description of Articles", Title, XBrushes.Black, new XPoint(180, 50));
        Block(g, 36, 70, 300, 34, "CASE CONTROL NUMBER", h.CaseControlNumber, false);
        Block(g, 36, 104, 300, 34, "RECEIVING ACTIVITY", h.ReceivingActivity, false);
        Block(g, 336, 104, 240, 34, "LOCATION", h.Location, false);
        Block(g, 36, 138, 300, 34, "NAME, GRADE AND TITLE OF PERSON FROM WHOM RECEIVED", h.ReceivedFrom, false);

        var y = 200.0;
        g.DrawRectangle(Rule, 36, y, 540, 26);
        g.DrawString("ITEM NO.", Label, XBrushes.Black, new XPoint(40, y + 17));
        g.DrawString("QUANTITY", Label, XBrushes.Black, new XPoint(90, y + 17));
        g.DrawString("DESCRIPTION OF ARTICLES", Label, XBrushes.Black, new XPoint(160, y + 17));
        y += 26;
        foreach (var item in items)
        {
            y += 16;
            g.DrawString(item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Value, XBrushes.Black, new XPoint(44, y));
            g.DrawString(item.Quantity, Value, XBrushes.Black, new XPoint(92, y));
            g.DrawString(item.Description, Value, XBrushes.Black, new XPoint(162, y));
        }

        y += 16;
        LastItem(g, y);
    }

    /// <summary>AR 195-5 2-3d: LAST ITEM centered, with lines drawn from the words to the margins.</summary>
    private static void LastItem(XGraphics g, double y)
    {
        g.DrawString("LAST ITEM", Value, XBrushes.Black, new XPoint(280, y));
        g.DrawLine(Rule, 40, y - 4, 272, y - 4);
        g.DrawLine(Rule, 338, y - 4, 572, y - 4);
    }

    private static void CustodyRows(XGraphics g, ref double y, Custody[] custody)
    {
        foreach (var c in custody)
        {
            y += 16;
            g.DrawString(c.ItemNumbers, Value, XBrushes.Black, new XPoint(44, y));
            g.DrawString(c.Date, Value, XBrushes.Black, new XPoint(92, y));
            g.DrawString(c.ReleasedBy, Value, XBrushes.Black, new XPoint(162, y));
            g.DrawString(c.ReceivedBy, Value, XBrushes.Black, new XPoint(302, y));
            g.DrawString(c.Purpose, Value, XBrushes.Black, new XPoint(442, y));
            y += 12;
            g.DrawString("SIGNATURE", Label, XBrushes.Black, new XPoint(162, y));
            g.DrawString("SIGNATURE", Label, XBrushes.Black, new XPoint(302, y));
            y += 10;
            g.DrawString("NAME, GRADE OR TITLE", Label, XBrushes.Black, new XPoint(162, y));
            g.DrawString("NAME, GRADE OR TITLE", Label, XBrushes.Black, new XPoint(302, y));
            y += 6;
            g.DrawLine(Rule, 36, y, 576, y);
        }
    }

    private static void Block(XGraphics g, double x, double y, double w, double h, string label, string value, bool unreadable)
    {
        g.DrawRectangle(Rule, x, y, w, h);
        g.DrawString(label, Label, XBrushes.Black, new XPoint(x + 3, y + 9));
        if (unreadable)
        {
            g.DrawRectangle(XBrushes.Black, x + 3, y + 13, w - 6, h - 16);
        }
        else if (value.Length > 0)
        {
            g.DrawString(value, Value, XBrushes.Black, new XPoint(x + 4, y + h - 8));
        }
    }
}
