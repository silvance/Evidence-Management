using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Reads;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Cases;

public class IndexModel : PageModel
{
    private readonly IEvidenceReadService _reads;
    private readonly ICaseService _cases;
    private readonly IEmcPageAuthorization _authorization;

    public IndexModel(
        IEvidenceReadService reads, ICaseService cases, IEmcPageAuthorization authorization)
    {
        _reads = reads;
        _cases = cases;
        _authorization = authorization;
    }

    public IReadOnlyList<CaseListRow> Cases { get; private set; } = [];
    public IReadOnlyList<EvidenceRoomOption> EvidenceRooms { get; private set; } = [];
    public bool CanCreateCase { get; private set; }
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public NewCaseInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _cases.CreateAsync(new CreateCaseRequest(
            Input.CaseControlNumber!, Input.Title!, Input.Synopsis, Input.EvidenceRoomId));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        return RedirectToPage("./Details", new { id = result.Value });
    }

    private async Task LoadAsync()
    {
        // The page holds no DbContext. Both calls authorize before querying, and scope to the
        // evidence rooms the user actually holds a grant in (IAM-016, IAM-017).
        EvidenceRooms = await _reads.GetAccessibleEvidenceRoomsAsync();

        if (Input.EvidenceRoomId == 0 && EvidenceRooms.Count > 0)
        {
            Input.EvidenceRoomId = EvidenceRooms[0].Id;
        }

        // Hiding the form is a usability affordance, never the control: CaseService authorizes
        // again on POST regardless of what the client submits.
        var decision = await _authorization.CheckAsync(
            EmcPermissions.CreateCase, Input.EvidenceRoomId == 0 ? null : Input.EvidenceRoomId);

        CanCreateCase = decision.IsAllowed;

        Cases = await _reads.GetAccessibleCasesAsync();
    }

    public sealed class NewCaseInput
    {
        /// <summary>AR 195-5 2-3b - the Army CI case control number (CASE-001).</summary>
        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "The case control number is required (AR 195-5 para 2-3b).")]
        [System.ComponentModel.DataAnnotations.StringLength(64)]
        public string? CaseControlNumber { get; set; }

        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "A case title is required.")]
        [System.ComponentModel.DataAnnotations.StringLength(512)]
        public string? Title { get; set; }

        [System.ComponentModel.DataAnnotations.StringLength(4000)]
        public string? Synopsis { get; set; }

        public int EvidenceRoomId { get; set; }
    }
}
