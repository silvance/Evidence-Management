using System.ComponentModel.DataAnnotations;
using Emc.Application.Authorization;
using Emc.Application.Documents;
using Emc.Application.Reads;
using Emc.Domain.Documents;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Documents;

/// <summary>
/// Upload a scanned document as a companion copy for a voucher. PDF only, validated by content.
/// The request-layer size limit here matches SourceDocumentOptions.MaxContentBytes' default; the
/// configured value is enforced again in the service.
/// </summary>
[RequestSizeLimit(52L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 50L * 1024 * 1024)]
public class UploadModel : PageModel
{
    private readonly ISourceDocumentService _documents;
    private readonly IEvidenceReadService _reads;
    private readonly IEmcPageAuthorization _authorization;

    public UploadModel(ISourceDocumentService documents, IEvidenceReadService reads, IEmcPageAuthorization authorization)
    {
        _documents = documents;
        _reads = reads;
        _authorization = authorization;
    }

    public int VoucherId { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public string VoucherIdentifier { get; private set; } = string.Empty;
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public UploadInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int voucherId)
        => await LoadAsync(voucherId) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(int voucherId)
    {
        if (!await LoadAsync(voucherId))
        {
            return NotFound();
        }

        if (!ModelState.IsValid || Input.File is null || Input.File.Length == 0)
        {
            Messages.Error = "Choose a PDF file to upload.";
            return Page();
        }

        byte[] bytes;
        await using (var stream = Input.File.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        var result = await _documents.UploadAsync(new UploadSourceDocumentRequest(
            EvidenceRoomId, null, voucherId, Input.DocumentType, Input.Provenance,
            Input.File.FileName, bytes, Input.ClassificationMarking ?? "UNCLASSIFIED", Input.ProvenanceNotes));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] = "Companion copy stored and hashed. It is not the original DA Form 4137.";
        TempData["Warnings"] = System.Text.Json.JsonSerializer.Serialize(result.Warnings);
        return RedirectToPage("/Documents/View", new { id = result.Value });
    }

    private async Task<bool> LoadAsync(int voucherId)
    {
        var view = await _reads.GetVoucherAsync(voucherId);
        if (view is null || !(await _authorization.CheckAsync(EmcPermissions.UploadSourceDocument, view.EvidenceRoomId)).IsAllowed)
        {
            return false;
        }

        VoucherId = view.Id;
        EvidenceRoomId = view.EvidenceRoomId;
        VoucherIdentifier = view.DisplayIdentifier;
        return true;
    }

    public sealed class UploadInput
    {
        public IFormFile? File { get; set; }
        public SourceDocumentType DocumentType { get; set; } = SourceDocumentType.DaForm4137;
        public ScanProvenance Provenance { get; set; } = ScanProvenance.Unknown;

        [StringLength(128)]
        public string? ClassificationMarking { get; set; } = "UNCLASSIFIED";

        [StringLength(2000)]
        public string? ProvenanceNotes { get; set; }
    }
}
