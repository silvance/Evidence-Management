using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages;

public class IndexModel : PageModel
{
    public void OnGet()
    {
        // Static content. The authoritative-record notice and classification banner are rendered
        // by the layout from SystemConfiguration (EMC-003, SEC-003).
    }
}
