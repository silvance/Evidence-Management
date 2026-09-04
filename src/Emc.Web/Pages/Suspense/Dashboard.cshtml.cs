using Emc.Application.Suspense;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Suspense;

/// <summary>
/// The suspense dashboard (AR 195-5 2-4f(3), 2-7a, 3-1a(4)): what is out of the room by the
/// regulation's three folders, days out, last contact, follow-ups due, and the LOCAL review
/// threshold - labelled as local, because the regulation sets no number of days. Read-only.
/// </summary>
public class DashboardModel : PageModel
{
    private readonly ISuspenseDashboardService _dashboard;

    public DashboardModel(ISuspenseDashboardService dashboard) => _dashboard = dashboard;

    public SuspenseDashboardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(int roomId)
    {
        View = await _dashboard.GetAsync(roomId);
        return View is null ? NotFound() : Page();
    }
}
