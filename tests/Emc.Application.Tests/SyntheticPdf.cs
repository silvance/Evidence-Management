using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Snippets.Font;
using PdfSharp.Pdf;

namespace Emc.Application.Tests;

/// <summary>
/// Synthetic PDFs for tests. Everything printed here is obviously fictitious - TEST case numbers,
/// TEST names, all-zero identifiers - because this repository is public and no real DA Form 4137
/// may ever appear in it.
/// </summary>
public static class SyntheticPdf
{
    static SyntheticPdf()
    {
        // PDFsharp needs a font resolver on Linux; the built-in one uses no system fonts and no network.
        if (GlobalFontSettings.FontResolver is null)
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = false;
            GlobalFontSettings.FontResolver = new FailsafeFontResolver();
        }
    }

    public static byte[] SinglePage(string text = "TEST DA FORM 4137 - FICTITIOUS - TEST-CI-2026-0001")
        => Build(1, text);

    public static byte[] Pages(int count, string text = "TEST PAGE")
        => Build(count, text);

    /// <summary>A page far larger than any scanner produces: 200 by 200 inches.</summary>
    public static byte[] PathologicalPage()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromInch(200);
        page.Height = XUnit.FromInch(200);
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Bytes that are not a PDF at all - a PNG header - regardless of what a file is named.</summary>
    public static byte[] FakePdf()
        => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89, 0, 0, 0, 0, (byte)'%', (byte)'%', (byte)'E', (byte)'O', (byte)'F'];

    private static byte[] Build(int pages, string text)
    {
        using var document = new PdfDocument();
        for (var i = 1; i <= pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromInch(8.5);
            page.Height = XUnit.FromInch(11);
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 14);
            gfx.DrawString($"{text} (page {i} of {pages})", font, XBrushes.Black, new XPoint(72, 72));
        }

        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
