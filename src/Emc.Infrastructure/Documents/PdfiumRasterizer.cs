using Emc.Application.Documents;
using PDFtoImage;
using SkiaSharp;

namespace Emc.Infrastructure.Documents;

/// <summary>
/// PDF page rendering with PDFium through the PDFtoImage/SkiaSharp packages. Everything arrives
/// through NuGet, native libraries included, so it flows through the same locked, hashed
/// dependency bundle as every other package (docs/air-gapped-build-and-maintenance.md).
///
/// PDFium is a parser of hostile input. This wrapper: opens from bytes only (no path, no URL);
/// renders one page at a time at a bounded DPI; honours cancellation between pages; and lets no
/// document content into any message it throws. The hard isolation - a separate process that can
/// crash without taking IIS down - is the OCR worker's job (Emc.OcrWorker); rendering moves there
/// with it.
/// </summary>
// PDFtoImage declares Windows, Linux and macOS support. The deployment target is Windows Server
// (IIS); the test lane runs on Linux. Both are in the supported set, and the bundle for each
// carries only that platform's native PDFium/Skia assets, so the analyzer's "all platforms"
// warning is suppressed for this one adapter rather than propagated through DI registration.
#pragma warning disable CA1416
public sealed class PdfiumRasterizer : IPdfRasterizer
{
    public string RendererVersion { get; } =
        $"PDFtoImage {typeof(Conversion).Assembly.GetName().Version?.ToString(3)} / PDFium (bblanchon) / SkiaSharp {typeof(SKBitmap).Assembly.GetName().Version?.ToString(3)}";

    public int GetPageCount(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            return Conversion.GetPageCount(pdf);
        }
        catch (Exception ex)
        {
            throw new MalformedPdfException("PDFium could not open the document.", ex);
        }
    }

    public IReadOnlyList<PdfPageDimensions> GetPageDimensions(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            var sizes = Conversion.GetPageSizes(pdf);
            return sizes.Select((s, i) => new PdfPageDimensions(i + 1, s.Width, s.Height)).ToList();
        }
        catch (Exception ex)
        {
            throw new MalformedPdfException("PDFium could not read the document's page sizes.", ex);
        }
    }

    public RenderedPage Render(byte[] pdf, int pageNumber, int dpi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ct.ThrowIfCancellationRequested();

        SKBitmap bitmap;
        try
        {
            bitmap = Conversion.ToImage(pdf, page: new Index(pageNumber - 1), options: new RenderOptions(Dpi: dpi, WithAnnotations: false, WithFormFill: false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MalformedPdfException($"PDFium could not render page {pageNumber}.", ex);
        }

        using (bitmap)
        {
            ct.ThrowIfCancellationRequested();
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new RenderedPage(pageNumber, bitmap.Width, bitmap.Height, dpi, data.ToArray());
        }
    }
}
#pragma warning restore CA1416
