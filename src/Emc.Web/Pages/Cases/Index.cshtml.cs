using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Emc.Web.Pages.Cases;

public class IndexModel : PageModel
{
    private readonly IEmcDbContext _db;
    private readonly ICaseService _cases;
    private readonly IEmcPageAuthorization _authorization;

    public IndexModel(IEmcDbContext db, ICaseService cases, IEmcPageAuthorization authorization)
    {
        _db = db;
        _cases = cases;
        _authorization = authorization;
    }

    public IReadOnlyList<CaseRow> Cases { get; private set; } = [];
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
        EvidenceRooms = await _db.EvidenceRooms
            .AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.Name)
            .Select(r => new EvidenceRoomOption(r.Id, r.Name))
            .ToListAsync();

        if (Input.EvidenceRoomId == 0 && EvidenceRooms.Count > 0)
        {
            Input.EvidenceRoomId = EvidenceRooms[0].Id;
        }

        // IAM-002 - resolved server-side. The form is not rendered at all without the permission,
        // and the service checks again on POST regardless of what the client submits.
        var decision = await _authorization.CheckAsync(
            EmcPermissions.CreateCase, Input.EvidenceRoomId == 0 ? null : Input.EvidenceRoomId);

        CanCreateCase = decision.IsAllowed;

        Cases = await _db.Cases
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new CaseRow(
                c.Id,
                c.CaseControlNumber,
                c.Title,
                c.IsClosed,
                c.Vouchers.Count))
            .ToListAsync();
    }

    public sealed record CaseRow(int Id, string CaseControlNumber, string Title, bool IsClosed, int VoucherCount);

    public sealed record EvidenceRoomOption(int Id, string Name);

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
