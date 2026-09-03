using System.ComponentModel.DataAnnotations;
using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Items;
using Emc.Application.Reads;
using Emc.Domain.Common;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Emc.Web.Pages.Items;

/// <summary>
/// An evidence item's complete chronological history, plus the location-assignment and
/// correction workflows.
///
/// The history includes superseded events. AR 195-5 para 2-5b(5) requires a struck-through
/// ledger entry to remain READABLE - so a corrected event is marked, never hidden (AUD-006).
/// </summary>
public class HistoryModel : PageModel
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceReadService _reads;
    private readonly IItemHistoryService _history;
    private readonly IEvidenceIntakeService _intake;
    private readonly IEmcPageAuthorization _authorization;

    public HistoryModel(
        IEmcDbContext db,
        IEvidenceReadService reads,
        IItemHistoryService history,
        IEvidenceIntakeService intake,
        IEmcPageAuthorization authorization)
    {
        _db = db;
        _reads = reads;
        _history = history;
        _intake = intake;
        _authorization = authorization;
    }

    public ItemHistoryView? View { get; private set; }
    public int VoucherId { get; private set; }
    public IReadOnlyList<LocationOption> Locations { get; private set; } = [];
    public IReadOnlyList<UserOption> Supervisors { get; private set; } = [];

    /// <summary>
    /// The fields the selected event allows to be corrected, keyed by event id. Rendered as a
    /// closed list so the form cannot name a field the server would reject (AUD-014).
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyCollection<string>> CorrectableFieldsByEvent
    { get; private set; } = new Dictionary<int, IReadOnlyCollection<string>>();
    public bool CanAssignLocation { get; private set; }
    public bool CanRecordCorrection { get; private set; }
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public LocationInput Location { get; set; } = new();

    [BindProperty]
    public CorrectionInput Correction { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAssignLocationAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(Location)))
        {
            return Page();
        }

        var occurredAtLocal = new DateTimeOffset(
            Location.OccurredAtLocal, TimeZoneInfo.Local.GetUtcOffset(Location.OccurredAtLocal));

        var result = await _intake.AssignStorageLocationAsync(new AssignLocationRequest(
            ItemId: id,
            StorageLocationId: Location.StorageLocationId,
            OccurredAtLocal: occurredAtLocal,
            Reason: Location.Reason,
            Notes: Location.Notes));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] = "Evidence-room location recorded. The previous location is retained.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRecordCorrectionAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(Correction)))
        {
            return Page();
        }

        // AUD-014. No original value is sent: the server derives it from the stored event, so a
        // correction cannot misstate what the record used to say.
        var result = await _history.RecordCorrectionAsync(new RecordCorrectionRequest(
            CorrectedEventId: Correction.CorrectedEventId,
            FieldName: Correction.FieldName!,
            CorrectedValue: Correction.CorrectedValue,
            Reason: Correction.Reason!,
            Category: CorrectionCategory.PostAcceptanceAccountabilityRecord,
            MfrReference: Correction.MfrReference,
            SupervisorNotifiedUserId: Correction.SupervisorNotifiedUserId,
            SupervisorNotifiedAtUtc: Correction.SupervisorNotifiedUserId is null
                ? null
                : DateTimeOffset.UtcNow));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] =
            "Correction recorded. The original entry is preserved and remains visible.";

        if (result.Warnings.Count > 0)
        {
            TempData["Warnings"] = System.Text.Json.JsonSerializer.Serialize(result.Warnings);
        }

        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Validates only the input object the submitted form actually posted.
    ///
    /// Both forms on this page bind on every POST, so the other form's [Required] attributes
    /// would otherwise leave ModelState invalid and silently block this submission - with no
    /// error visible, because the other form's validation spans are not rendered. Entries
    /// outside the prefix are removed before the check.
    /// </summary>
    private bool IsValidForPrefix(string prefix)
    {
        foreach (var key in ModelState.Keys
                     .Where(k => !k.StartsWith(prefix, StringComparison.Ordinal))
                     .ToList())
        {
            ModelState.Remove(key);
        }

        return ModelState.IsValid;
    }

    private async Task<bool> LoadAsync(int id)
    {
        View = await _history.GetAsync(id);
        if (View is null)
        {
            return false;
        }

        // The history service already authorized the read; this resolves the owning room for the
        // write-permission checks below, and re-checks read permission on the way.
        var evidenceRoomId = await _reads.GetReadableItemEvidenceRoomIdAsync(id);
        if (evidenceRoomId is null)
        {
            return false;
        }

        VoucherId = await _db.EvidenceItems
            .AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => i.VoucherId)
            .FirstAsync();

        CanAssignLocation =
            (await _authorization.CheckAsync(EmcPermissions.AssignStorageLocation, evidenceRoomId))
            .IsAllowed;

        CanRecordCorrection =
            (await _authorization.CheckAsync(EmcPermissions.RecordCorrection, evidenceRoomId))
            .IsAllowed;

        var locations = await _db.StorageLocations
            .AsNoTracking()
            .Include(l => l.Parent)
            .Where(l => l.EvidenceRoomId == evidenceRoomId && l.IsActive)
            .ToListAsync();

        Locations = locations
            .Select(l => new LocationOption(l.Id, l.FullPath))
            .OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // AR 195-5 para 1-7c(3) - the responsible CI supervisor who must be informed immediately
        // when an incorrect entry is found.
        Supervisors = await _db.Users
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new UserOption(u.Id, u.DisplayName))
            .ToListAsync();

        CorrectableFieldsByEvent = View.History
            .Where(r => r.Kind != ItemEventKind.Correction)
            .ToDictionary(r => r.EventId, r => (IReadOnlyCollection<string>)r.EffectiveFields.Keys.ToList());

        if (Location.OccurredAtLocal == default)
        {
            Location.OccurredAtLocal = DateTime.Now;
        }

        if (TempData["Success"] is string success)
        {
            Messages.Success = success;
        }

        if (TempData["Warnings"] is string packed && packed.Length > 0)
        {
            Messages.Warnings =
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(packed) ?? [];
        }

        return true;
    }

    public sealed record LocationOption(int Id, string Path);

    public sealed record UserOption(int Id, string DisplayName);

    public sealed class LocationInput
    {
        [Range(1, int.MaxValue, ErrorMessage = "Select a storage location.")]
        public int StorageLocationId { get; set; }

        public DateTime OccurredAtLocal { get; set; }

        [StringLength(1000)]
        public string? Reason { get; set; }

        [StringLength(4000)]
        public string? Notes { get; set; }
    }

    public sealed class CorrectionInput
    {
        [Range(1, int.MaxValue, ErrorMessage = "Select the entry to correct.")]
        public int CorrectedEventId { get; set; }

        [Required(ErrorMessage = "Name the field being corrected.")]
        [StringLength(128)]
        public string? FieldName { get; set; }

        [StringLength(4000)]
        public string? CorrectedValue { get; set; }

        /// <summary>AR 195-5 para 1-7c(3) - the corrective action, documented (AUD-004).</summary>
        [Required(ErrorMessage = "A reason is required (AR 195-5 para 1-7c(3)).")]
        [StringLength(2000)]
        public string? Reason { get; set; }

        /// <summary>AR 195-5 para 1-7c(3) - the MFR filed with the DA Form 4137 (AUD-005).</summary>
        [StringLength(256)]
        public string? MfrReference { get; set; }

        /// <summary>AR 195-5 para 1-7c(3) - the supervisor informed immediately (AUD-005).</summary>
        public int? SupervisorNotifiedUserId { get; set; }
    }
}
