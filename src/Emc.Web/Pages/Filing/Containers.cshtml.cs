using System.ComponentModel.DataAnnotations;
using Emc.Application.Authorization;
using Emc.Application.Filing;
using Emc.Domain.Filing;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Filing;

/// <summary>
/// The evidence room's paper DA Form 4137 files: active folders/binders (AR 195-5 2-4f(1)), the
/// three suspense folders (2-4f(3)) and inactive files by disposition month (2-4h).
/// </summary>
public class ContainersModel : PageModel
{
    private readonly IPhysicalDocumentService _physical;
    private readonly IEmcPageAuthorization _authorization;

    public ContainersModel(IPhysicalDocumentService physical, IEmcPageAuthorization authorization)
    {
        _physical = physical;
        _authorization = authorization;
    }

    public int EvidenceRoomId { get; private set; }
    public IReadOnlyList<FileContainerRow> Containers { get; private set; } = [];
    public bool CanManage { get; private set; }
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public NewContainerInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int roomId)
        => await LoadAsync(roomId) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync(int roomId)
    {
        if (!await LoadAsync(roomId))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _physical.CreateContainerAsync(new CreateFileContainerRequest(
            roomId, Input.Kind, Input.Form, Input.Label!, Input.RangeCalendarYear, Input.RangeFromSequence, Input.RangeToSequence,
            Input.DispositionYear, Input.DispositionMonth, Input.Notes));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] = "File container recorded.";
        return RedirectToPage(new { roomId });
    }

    private async Task<bool> LoadAsync(int roomId)
    {
        // Read authorization first; an unauthorized room reads as absent (IAM-018).
        if (!(await _authorization.CheckAsync(EmcPermissions.ViewVoucher, roomId)).IsAllowed)
        {
            return false;
        }

        EvidenceRoomId = roomId;
        Containers = await _physical.GetContainersAsync(roomId);
        CanManage = (await _authorization.CheckAsync(EmcPermissions.ManagePhysicalFiles, roomId)).IsAllowed;

        if (TempData["Success"] is string success)
        {
            Messages.Success = success;
        }

        return true;
    }

    public sealed class NewContainerInput
    {
        public PhysicalFileKind Kind { get; set; } = PhysicalFileKind.Active4137File;
        public ContainerForm Form { get; set; } = ContainerForm.Folder;

        [Required, StringLength(256)]
        public string? Label { get; set; }

        /// <summary>Active files (para 2-4f(1)): the calendar year and the first and last document sequence. Rendered in the room's layout for the label.</summary>
        [Range(1990, 2200)]
        public int? RangeCalendarYear { get; set; }

        [Range(1, 999999)]
        public int? RangeFromSequence { get; set; }

        [Range(1, 999999)]
        public int? RangeToSequence { get; set; }

        public int? DispositionYear { get; set; }
        public int? DispositionMonth { get; set; }

        [StringLength(2000)]
        public string? Notes { get; set; }
    }
}
