using System.Text.Json;
using Emc.Application.Documents;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Documents;

/// <summary>
/// A source document: metadata and server-rendered page images (DOC-005). The page-image and
/// download handlers authorize on the owning room BEFORE any bytes are read, and answer 404 for
/// anything unauthorized or absent, identically (IAM-018). The original PDF is never sent to the
/// browser for display; the download handler sends it as an attachment only, under the separate
/// download permission, audited (DOC-009).
/// </summary>
public class ViewModel : PageModel
{
    private readonly ISourceDocumentService _documents;

    public ViewModel(ISourceDocumentService documents) => _documents = documents;

    public SourceDocumentView? Document { get; private set; }
    public PageMessages Messages { get; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Document = await _documents.GetAsync(id);
        if (Document is null)
        {
            return NotFound();
        }

        if (TempData["Success"] is string success)
        {
            Messages.Success = success;
        }

        if (TempData["Warnings"] is string packed && packed.Length > 0)
        {
            Messages.Warnings = JsonSerializer.Deserialize<List<string>>(packed) ?? [];
        }

        return Page();
    }

    // The parameter is "pageNumber", not "page": "page" is the Razor Pages route key that names
    // the page itself, and a query value under that name never reaches the handler.
    public async Task<IActionResult> OnGetPageAsync(int id, int pageNumber)
    {
        var stream = await _documents.OpenPageImageAsync(id, pageNumber);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return File(stream, "image/png");
    }

    public async Task<IActionResult> OnGetDownloadAsync(int id)
    {
        var stream = await _documents.OpenOriginalForDownloadAsync(id);
        if (stream is null)
        {
            return NotFound();
        }

        // Attachment, under a generated name: the original filename is metadata and never goes
        // into a header. No inline rendering of the PDF by the browser.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, "application/pdf", $"source-document-{id}.pdf");
    }
}
