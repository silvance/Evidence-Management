namespace Emc.Web.Pages;

/// <summary>
/// Messages surfaced on a page. Warnings are deliberately distinct from errors: several
/// AR 195-5-derived checks are advisory by design because EMC is a companion and the custodian
/// holds the authoritative record (VCH-009, ITEM-003, IAM-006, AUD-005).
/// </summary>
public sealed class PageMessages
{
    public string? Error { get; set; }
    public string? RequirementId { get; set; }
    public string? Success { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
