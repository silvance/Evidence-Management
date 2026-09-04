using System.Globalization;
using System.Text.Json;
using Emc.Application.Documents;
using Emc.Infrastructure.Documents;

namespace Emc.OcrWorker;

/// <summary>
/// The worker's "render" mode: the killable child process that <c>IsolatedPdfRasterizer</c>
/// starts per invocation (DOC-014). No host, no configuration, no database, no network: it reads
/// one file named on its argument list, does one thing with PDFium and writes one file.
///
/// <c>Emc.OcrWorker render info --input FILE --output FILE</c> writes a JSON manifest of page
/// count and page sizes. <c>Emc.OcrWorker render page --page N --dpi D --input FILE --output
/// FILE</c> writes page N as a PNG. Output is written to a partial name and moved into place, so
/// the parent never reads a half-written file. Exit codes are categories: 0 ok, 2 the bytes could
/// not be opened as a PDF, 3 bad arguments, 1 anything else. Nothing about the document is
/// written to stdout or stderr - the parent discards both anyway.
/// </summary>
internal static class RenderHelper
{
    public static int Run(string[] args)
    {
        string? mode = null, input = null, output = null;
        var page = 0;
        var dpi = 0;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "info":
                case "page":
                    mode = args[i];
                    break;
                case "--input":
                    input = Next(args, ref i);
                    break;
                case "--output":
                    output = Next(args, ref i);
                    break;
                case "--page":
                    if (!int.TryParse(Next(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out page)) { return IsolatedPdfRasterizer.ExitUsage; }
                    break;
                case "--dpi":
                    if (!int.TryParse(Next(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out dpi)) { return IsolatedPdfRasterizer.ExitUsage; }
                    break;
                default:
                    return IsolatedPdfRasterizer.ExitUsage;
            }
        }

        if (mode is null || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output)
            || (mode == "page" && (page < 1 || dpi is < 36 or > 600)))
        {
            return IsolatedPdfRasterizer.ExitUsage;
        }

        try
        {
            var pdf = File.ReadAllBytes(input);
            var rasterizer = new PdfiumRasterizer();
            var partial = output + ".partial";

            if (mode == "info")
            {
                var count = rasterizer.GetPageCount(pdf);
                var sizes = rasterizer.GetPageDimensions(pdf);
                var manifest = new IsolatedPdfRasterizer.HelperInfo
                {
                    PageCount = count,
                    Pages = sizes.Select(s => new IsolatedPdfRasterizer.HelperPage { PageNumber = s.PageNumber, WidthPoints = s.WidthPoints, HeightPoints = s.HeightPoints }).ToList()
                };
                File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(manifest));
            }
            else
            {
                var rendered = rasterizer.Render(pdf, page, dpi);
                File.WriteAllBytes(partial, rendered.Png);
            }

            File.Move(partial, output, overwrite: false);
            return IsolatedPdfRasterizer.ExitOk;
        }
        catch (MalformedPdfException)
        {
            return IsolatedPdfRasterizer.ExitMalformed;
        }
        catch (Exception)
        {
            return IsolatedPdfRasterizer.ExitUnexpected;
        }
    }

    private static string? Next(string[] args, ref int i) => ++i < args.Length ? args[i] : null;
}
