using Emc.Application.Filing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Filing;

/// <summary>
/// The paper DA Form 4137 files as a dashboard (AR 195-5 2-4f, 2-4h): Active, Suspense, and
/// Inactive split into Retain / Eligible for destruction / Destruction confirmed by the 3-year
/// clock that runs from the inactive date alone. Read-only; every act is recorded on the voucher.
/// </summary>
public class RetentionModel : PageModel
{
    private readonly IRetentionDashboardService _dashboard;

    public RetentionModel(IRetentionDashboardService dashboard) => _dashboard = dashboard;

    public RetentionDashboardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(int roomId)
    {
        View = await _dashboard.GetAsync(roomId);
        return View is null ? NotFound() : Page();
    }
}
