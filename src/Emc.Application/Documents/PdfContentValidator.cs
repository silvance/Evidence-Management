using System.Text;

namespace Emc.Application.Documents;

public sealed record PdfContentValidation(bool IsValid, string? RequirementId, string? Error, bool ContainsActiveContent)
{
    public static PdfContentValidation Ok(bool activeContent) => new(true, null, null, activeContent);
    public static PdfContentValidation Fail(string requirementId, string error) => new(false, requirementId, error, false);
}

/// <summary>
/// Validates an upload by CONTENT (DOC-003). The client's filename extension and content type are
/// not consulted: a PNG named ".pdf" is rejected and a PDF named ".jpg" is accepted. The check
/// here is structural - the PDF header near the start, the end-of-file marker near the end,
/// within size - and the rasterizer's ability to open the file is the second gate. Active content
/// (JavaScript, launch actions, open actions) is detected and reported but never executed: EMC
/// only ever shows server-rendered page images (DOC-005).
/// </summary>
public static class PdfContentValidator
{
    private const int HeaderWindow = 1024;
    private const int TrailerWindow = 2048;

    public static PdfContentValidation Validate(ReadOnlySpan<byte> bytes, long maxContentBytes)
    {
        if (bytes.Length == 0)
        {
            return PdfContentValidation.Fail("DOC-003", "The upload is empty.");
        }

        if (bytes.Length > maxContentBytes)
        {
            return PdfContentValidation.Fail(
                "DOC-004", $"The upload is {bytes.Length:N0} bytes; the limit is {maxContentBytes:N0} bytes.");
        }

        var head = bytes[..Math.Min(HeaderWindow, bytes.Length)];
        var headerAt = head.IndexOf("%PDF-"u8);
        if (headerAt < 0 || headerAt + 8 > head.Length
            || !char.IsAsciiDigit((char)head[headerAt + 5]) || head[headerAt + 6] != (byte)'.' || !char.IsAsciiDigit((char)head[headerAt + 7]))
        {
            return PdfContentValidation.Fail(
                "DOC-003", "The upload is not a PDF: no PDF header was found in the first kilobyte. The file's name and declared type are not used to decide this.");
        }

        var tail = bytes[Math.Max(0, bytes.Length - TrailerWindow)..];
        if (tail.IndexOf("%%EOF"u8) < 0)
        {
            return PdfContentValidation.Fail(
                "DOC-003", "The upload is not a complete PDF: no end-of-file marker was found near the end.");
        }

        return PdfContentValidation.Ok(ContainsActiveContentMarker(bytes));
    }

    /// <summary>
    /// PDF name tokens that introduce scripting, launch actions, remote go-to, open actions,
    /// additional actions, URIs or rich media. Their presence is reported as a warning; a
    /// scanned form contains none of them.
    /// </summary>
    private static readonly byte[][] ActiveContentMarkers =
    [
        "/JavaScript"u8.ToArray(), "/JS"u8.ToArray(), "/Launch"u8.ToArray(), "/OpenAction"u8.ToArray(),
        "/AA"u8.ToArray(), "/URI"u8.ToArray(), "/GoToR"u8.ToArray(), "/RichMedia"u8.ToArray()
    ];

    private static bool ContainsActiveContentMarker(ReadOnlySpan<byte> haystack)
    {
        foreach (var needle in ActiveContentMarkers)
        {
            if (haystack.IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
