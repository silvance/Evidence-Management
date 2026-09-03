using System.ComponentModel.DataAnnotations;
using Emc.Application.Authorization;
using Emc.Application.Documents;
using Emc.Application.Ocr;
using Emc.Domain.Ocr;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Documents;

/// <summary>
/// Human verification of an OCR run (OCR-005): every extracted field beside the image the
/// engine read, each with the decision a person makes about it. This page records
/// VERIFICATIONS ONLY. It never touches the voucher, an item, or any accountability record;
/// that is reconciliation, a separate page and a separate, explicit act (REC-001).
/// </summary>
public class VerifyModel : PageModel
{
    private readonly ISourceDocumentService _documents;
    private readonly IOcrJobService _ocr;
    private readonly IEmcPageAuthorization _authorization;

    public VerifyModel(ISourceDocumentService documents, IOcrJobService ocr, IEmcPageAuthorization authorization)
    {
        _documents = documents;
        _ocr = ocr;
        _authorization = authorization;
    }

    public const string CompanionStatement =
        "DIGITAL SCAN IS A COMPANION COPY AND THE PHYSICAL ORIGINAL DA FORM 4137 REMAINS AUTHORITATIVE";

    public SourceDocumentView? Document { get; private set; }
    public OcrRunView? Run { get; private set; }
    public bool CanVerify { get; private set; }
    public PageMessages Messages { get; } = new();

    /// <summary>Display scale for the run's page images: the engine's 300 DPI image shown at a quarter, boxes scaled the same.</summary>
    public const double DisplayScale = 0.25;

    [BindProperty]
    public VerifyInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (TempData["Success"] is string success)
        {
            Messages.Success = success;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!CanVerify)
        {
            return NotFound();
        }

        var result = await _ocr.VerifyFieldAsync(new VerifyFieldRequest(Input.FieldId, Input.Decision, Input.EnteredValue, Input.Note));
        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] = $"Verification recorded for field {Input.FieldId}. The raw extraction is unchanged; nothing on the accountability record has changed.";
        return RedirectToPage("/Documents/Verify", pageHandler: null, routeValues: new { id }, fragment: $"field-{Input.FieldId}");
    }

    public async Task<IActionResult> OnGetRunPageAsync(int id, int runId, int pageNumber)
    {
        var stream = await _ocr.OpenRunPageImageAsync(runId, pageNumber);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return File(stream, "image/png");
    }

    private async Task<bool> LoadAsync(int id)
    {
        Document = await _documents.GetAsync(id);
        if (Document is null)
        {
            return false;
        }

        var status = await _ocr.GetStatusAsync(id);
        Run = status?.LatestRun;
        CanVerify = (await _authorization.CheckAsync(EmcPermissions.VerifyOcr, Document.EvidenceRoomId)).IsAllowed;
        return true;
    }

    public static string BandLabel(ConfidenceBand band) => band switch
    {
        ConfidenceBand.High => "High",
        ConfidenceBand.Medium => "Medium — flagged",
        _ => "Low / unreadable — enter from the paper"
    };

    public sealed class VerifyInput
    {
        public int FieldId { get; set; }
        public FieldVerificationDecision Decision { get; set; } = FieldVerificationDecision.AcceptedAsRead;

        [StringLength(4000)]
        public string? EnteredValue { get; set; }

        [StringLength(2000)]
        public string? Note { get; set; }
    }
}
