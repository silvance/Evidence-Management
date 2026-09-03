using System.ComponentModel.DataAnnotations;
using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Domain.Common;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Emc.Web.Pages.Cases;

public class DetailsModel : PageModel
{
    private readonly IEmcDbContext _db;
    private readonly IVoucherService _vouchers;
    private readonly IEmcPageAuthorization _authorization;

    public DetailsModel(IEmcDbContext db, IVoucherService vouchers, IEmcPageAuthorization authorization)
    {
        _db = db;
        _vouchers = vouchers;
        _authorization = authorization;
    }

    public string CaseControlNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string? Synopsis { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public IReadOnlyList<VoucherRow> Vouchers { get; private set; } = [];
    public bool CanCreateVoucher { get; private set; }
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public NewVoucherInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Input.AcquiredAtLocal = DateTime.Now;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // AR 195-5 2-3b - a request for assistance records BOTH the seizing and the requesting
        // office's case numbers (CASE-002).
        if (Input.IsRequestForAssistance && string.IsNullOrWhiteSpace(Input.RequestingOfficeCaseNumber))
        {
            Messages.Error =
                "AR 195-5 para 2-3b: when evidence is collected in response to a request for "
                + "assistance, both the seizing and the requesting office's case control numbers "
                + "must be recorded.";
            Messages.RequirementId = "CASE-002";
            return Page();
        }

        var acquiredAtLocal = new DateTimeOffset(
            Input.AcquiredAtLocal, TimeZoneInfo.Local.GetUtcOffset(Input.AcquiredAtLocal));

        var result = await _vouchers.CreateDraftAsync(new CreateVoucherRequest(
            CaseId: id,
            ReceivingActivity: Input.ReceivingActivity!,
            ReceivingActivityLocation: Input.ReceivingActivityLocation!,
            ReceivedFrom: Input.ReceivedFrom!,
            AcquiredAtLocal: acquiredAtLocal,
            IsRequestForAssistance: Input.IsRequestForAssistance,
            RequestingOfficeCaseNumber: Input.RequestingOfficeCaseNumber));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        return RedirectToPage("/Vouchers/Details", new { id = result.Value });
    }

    private async Task<bool> LoadAsync(int id)
    {
        var owningCase = await _db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (owningCase is null)
        {
            return false;
        }

        CaseControlNumber = owningCase.CaseControlNumber;
        Title = owningCase.Title;
        Synopsis = owningCase.Synopsis;
        EvidenceRoomId = owningCase.EvidenceRoomId;

        CanCreateVoucher =
            (await _authorization.CheckAsync(EmcPermissions.CreateDraftVoucher, EvidenceRoomId))
            .IsAllowed;

        var vouchers = await _db.EvidenceVouchers
            .AsNoTracking()
            .Include(v => v.Items)
            .Include(v => v.DocumentNumberAssignments)
            .Where(v => v.CaseId == id)
            .OrderBy(v => v.CreatedAtUtc)
            .ToListAsync();

        Vouchers = vouchers
            .Select(v => new VoucherRow(
                v.Id,
                v.DisplayIdentifier,
                v.HasOfficialDocumentNumber,

                // VCH-007 - derived from the items, never a stored column (AR 195-5 2-4h).
                v.DerivedStatus,
                v.Items.Count,
                v.AcquiredAtLocal))
            .ToList();

        return true;
    }

    public sealed record VoucherRow(
        int Id,
        string DisplayIdentifier,
        bool HasOfficialDocumentNumber,
        VoucherDerivedStatus DerivedStatus,
        int ItemCount,
        DateTimeOffset AcquiredAtLocal);

    public sealed class NewVoucherInput
    {
        [Required(ErrorMessage = "The receiving activity is required.")]
        [StringLength(256)]
        public string? ReceivingActivity { get; set; }

        [Required(ErrorMessage = "The receiving activity's location is required.")]
        [StringLength(256)]
        public string? ReceivingActivityLocation { get; set; }

        /// <summary>AR 195-5 2-3b / App B-4a(7)(c) - the person or place from whom received.</summary>
        [Required(ErrorMessage = "The person or place from whom the evidence was received is required (AR 195-5 para 2-3b).")]
        [StringLength(512)]
        public string? ReceivedFrom { get; set; }

        [Required]
        public DateTime AcquiredAtLocal { get; set; }

        public bool IsRequestForAssistance { get; set; }

        [StringLength(64)]
        public string? RequestingOfficeCaseNumber { get; set; }
    }
}
