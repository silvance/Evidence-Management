using System.ComponentModel.DataAnnotations;
using Emc.Application.Reconciliation;
using Emc.Domain.Reconciliation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Documents;

/// <summary>
/// Reconciliation (REC-001 to REC-004): the verified scan against the companion record, one
/// difference at a time, each with an explicit decision. Before acceptance a decision may change
/// the DRAFT; after acceptance every decision is a finding, and a true error goes to the para
/// 1-7c(3) correction on the item's history page with this scan as provenance.
/// </summary>
public class ReconcileModel : PageModel
{
    private readonly IReconciliationService _reconciliation;

    public ReconcileModel(IReconciliationService reconciliation) => _reconciliation = reconciliation;

    public ReconciliationView? View { get; private set; }
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public DecisionInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        View = await _reconciliation.GetAsync(id);
        if (View is null)
        {
            return NotFound();
        }

        if (TempData["Success"] is string success)
        {
            Messages.Success = success;
        }

        if (TempData["Warnings"] is string packed && packed.Length > 0)
        {
            Messages.Warnings = System.Text.Json.JsonSerializer.Deserialize<List<string>>(packed) ?? [];
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDecideAsync(int id)
    {
        View = await _reconciliation.GetAsync(id);
        if (View is null)
        {
            return NotFound();
        }

        var result = await _reconciliation.DecideAsync(new ReconciliationDecisionRequest(id, Input.FieldKey ?? string.Empty, Input.Decision, Input.Narrative));
        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] = $"Decision recorded (finding {result.Value}): {Input.Decision} on {Input.FieldKey}.";
        TempData["Warnings"] = System.Text.Json.JsonSerializer.Serialize(result.Warnings.ToList());
        return RedirectToPage("/Documents/Reconcile", new { id });
    }

    public sealed class DecisionInput
    {
        [Required]
        [StringLength(128)]
        public string? FieldKey { get; set; }

        public ReconciliationDecision Decision { get; set; } = ReconciliationDecision.CompanionRecordAlreadyCorrect;

        [StringLength(4000)]
        public string? Narrative { get; set; }
    }
}
