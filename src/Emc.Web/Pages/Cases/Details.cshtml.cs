using System.ComponentModel.DataAnnotations;
using Emc.Application.Authorization;
using Emc.Application.Reads;
using Emc.Application.Time;
using Emc.Application.Cases;
using Emc.Domain.Common;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Cases;

public class DetailsModel : PageModel
{
    private readonly IEvidenceReadService _reads;
    private readonly IVoucherService _vouchers;
    private readonly IEmcPageAuthorization _authorization;

    private readonly IEvidenceRoomTimeService _time;

    public DetailsModel(
        IEvidenceReadService reads, IVoucherService vouchers, IEmcPageAuthorization authorization,
        IEvidenceRoomTimeService time)
    {
        _reads = reads;
        _vouchers = vouchers;
        _authorization = authorization;
        _time = time;
    }

    public string CaseControlNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string? Synopsis { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public IReadOnlyList<VoucherListRow> Vouchers { get; private set; } = [];
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

        // The room's wall clock now, from the application clock - not the host's.
        Input.AcquiredAtLocal = (await _time.NowInRoomAsync(EvidenceRoomId)).DateTime;
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

        // AUD-011 / AUD-020. Interpreted in the evidence room's zone, never the web server's;
        // a time in the repeated or skipped DST hour is refused with an explanation.
        var acquired = await _time.ResolveLocalAsync(
            EvidenceRoomId, Input.AcquiredAtLocal, Input.AmbiguousTimeChoice);

        if (!acquired.Succeeded)
        {
            Messages.Error = acquired.Error;
            Messages.RequirementId = acquired.RequirementId;
            return Page();
        }

        var acquiredAtLocal = acquired.Value!.Value;

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
        // Authorizes before returning anything, and returns null when the caller may not read
        // the case - so guessing identifiers cannot confirm which cases exist (IAM-018).
        var view = await _reads.GetCaseAsync(id);
        if (view is null)
        {
            return false;
        }

        CaseControlNumber = view.CaseControlNumber;
        Title = view.Title;
        Synopsis = view.Synopsis;
        EvidenceRoomId = view.EvidenceRoomId;
        Vouchers = view.Vouchers;

        CanCreateVoucher =
            (await _authorization.CheckAsync(EmcPermissions.CreateDraftVoucher, EvidenceRoomId))
            .IsAllowed;

        return true;
    }

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

        /// <summary>Only for a time in the hour repeated when clocks fall back (AUD-020).</summary>
        public AmbiguousLocalTimeChoice AmbiguousTimeChoice { get; set; }

        public bool IsRequestForAssistance { get; set; }

        [StringLength(64)]
        public string? RequestingOfficeCaseNumber { get; set; }
    }
}
