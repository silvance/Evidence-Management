using System.ComponentModel.DataAnnotations;
using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Items;
using Emc.Application.Reads;
using Emc.Application.Time;
using Emc.Domain.Common;
using Emc.Domain.Events;
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
    private readonly IClock _clock;

    private readonly IEvidenceRoomTimeService _time;
    private readonly ICustodyEventService _custody;

    public HistoryModel(
        IEmcDbContext db,
        IEvidenceReadService reads,
        IItemHistoryService history,
        IEvidenceIntakeService intake,
        IEmcPageAuthorization authorization,
        IClock clock,
        IEvidenceRoomTimeService time,
        ICustodyEventService custody)
    {
        _custody = custody;
        _db = db;
        _reads = reads;
        _history = history;
        _intake = intake;
        _authorization = authorization;
        _clock = clock;
        _time = time;
    }

    public ItemHistoryView? View { get; private set; }
    public int VoucherId { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public IReadOnlyList<LocationOption> Locations { get; private set; } = [];
    public IReadOnlyList<UserOption> Supervisors { get; private set; } = [];

    /// <summary>
    /// The fields the selected event allows to be corrected, keyed by event id. Rendered as a
    /// closed list so the form cannot name a field the server would reject (AUD-014).
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyCollection<string>> CorrectableFieldsByEvent
    { get; private set; } = new Dictionary<int, IReadOnlyCollection<string>>();

    /// <summary>
    /// Correctable fields that name a STORAGE LOCATION rather than holding text. A correction to
    /// one of these selects the replacement location; typing a new path is rejected, because the
    /// record's own projections resolve the location by identifier (AUD-016).
    /// </summary>
    public IReadOnlyCollection<string> LocationReferenceFieldNames { get; private set; } = [];
    public bool CanAssignLocation { get; private set; }
    public bool CanRecordCorrection { get; private set; }
    public bool CanRecordCustody { get; private set; }
    public IReadOnlyList<UserOption> Users { get; private set; } = [];
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public CustodyInput Custody { get; set; } = new();

    [BindProperty]
    public LocationInput Location { get; set; } = new();

    [BindProperty]
    public CorrectionInput Correction { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, int? sourceDocumentId = null, int? correctedEventId = null, string? fieldName = null, string? correctedValue = null,
        int? findingId = null, string? custodyDate = null, string? releasedByName = null, string? receivedByName = null, string? purpose = null)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        // Reconciliation hands a custody row over here (REC-010). The reading is shown as a
        // default the custodian confirms or changes; nothing is recorded until they do.
        if (findingId is not null)
        {
            Custody.SourceDocumentId = sourceDocumentId;
            Custody.ReconciliationFindingId = findingId;
            Custody.ReleasedByName = releasedByName;
            Custody.ReceivedByName = receivedByName;
            Custody.Purpose = purpose;
            if (custodyDate is not null && DateTime.TryParseExact(custodyDate, ["dd MMM yy", "dd MMM yyyy", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                Custody.OccurredAtLocal = parsed;
            }
        }

        // Reconciliation hands over here with the scan as provenance and the difference prefilled
        // (REC-006). Nothing is recorded until the custodian completes the 1-7c(3) form below.
        if (sourceDocumentId is not null)
        {
            Correction.SourceDocumentId = sourceDocumentId;
            if (correctedEventId is not null) Correction.CorrectedEventId = correctedEventId.Value;
            if (fieldName is not null) Correction.FieldName = fieldName;
            if (correctedValue is not null) Correction.CorrectedValue = correctedValue;
        }

        return Page();
    }

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

        // AUD-011 / AUD-020. Interpreted in the evidence room's zone, never the web server's;
        // a time in the repeated or skipped DST hour is refused with an explanation.
        var occurred = await _time.ResolveLocalAsync(
            EvidenceRoomId, Location.OccurredAtLocal, Location.AmbiguousTimeChoice);

        if (!occurred.Succeeded)
        {
            Messages.Error = occurred.Error;
            Messages.RequirementId = occurred.RequirementId;
            return Page();
        }

        var occurredAtLocal = occurred.Value!.Value;

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

    /// <summary>REC-010 / COC-002: a change of custody the paper shows, recorded by the custodian with the paper's date; no status change, no release.</summary>
    public async Task<IActionResult> OnPostRecordCustodyAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(Custody)))
        {
            return Page();
        }

        var occurred = await _time.ResolveLocalAsync(EvidenceRoomId, Custody.OccurredAtLocal, Custody.AmbiguousTimeChoice);
        if (!occurred.Succeeded)
        {
            Messages.Error = occurred.Error;
            Messages.RequirementId = occurred.RequirementId;
            return Page();
        }

        var result = await _custody.RecordHistoricalCustodyEventAsync(new RecordHistoricalCustodyEventRequest(
            id,
            Party(Custody.ReleasedByKind, Custody.ReleasedByName, Custody.ReleasedByUserId, Custody.ReleasedByTitleOrGrade, Custody.ReleasedByOrganization, Custody.ReleasedByMailNumber),
            Party(Custody.ReceivedByKind, Custody.ReceivedByName, Custody.ReceivedByUserId, Custody.ReceivedByTitleOrGrade, Custody.ReceivedByOrganization, Custody.ReceivedByMailNumber),
            Custody.Purpose ?? string.Empty, occurred.Value!.Value, Custody.IsScrcni, Custody.Destination, Custody.Agency, Custody.Notes,
            Custody.SourceDocumentId, Custody.ReconciliationFindingId));

        if (!result.Succeeded)
        {
            Messages.Error = result.Error;
            Messages.RequirementId = result.RequirementId;
            return Page();
        }

        TempData["Success"] = "Change of custody recorded on the item's chain as of the date the paper shows.";
        TempData["Warnings"] = System.Text.Json.JsonSerializer.Serialize(result.Warnings.ToList());
        return RedirectToPage(new { id });

        static CustodyPartyInput Party(CustodyPartyKind kind, string? name, int? userId, string? title, string? organization, string? mail)
            => new(kind, name, userId, title, organization, IdentificationVerified: true, mail);
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
            CorrectedReferenceId: Correction.CorrectedStorageLocationId,
            SupervisorNotifiedName: Correction.SupervisorNotifiedName,
            SupervisorNotifiedGradeOrTitle: Correction.SupervisorNotifiedGradeOrTitle,
            SupervisorNotifiedOrganization: Correction.SupervisorNotifiedOrganization,

            // The clock is injected so the recorded moment is the application's, testable and
            // consistent with every other timestamp EMC writes - never the web server's ambient
            // DateTimeOffset.UtcNow.
            SupervisorNotifiedAtUtc:
                Correction.SupervisorNotifiedUserId is null
                && string.IsNullOrWhiteSpace(Correction.SupervisorNotifiedName)
                    ? null
                    : _clock.UtcNow,
            SourceDocumentId: Correction.SourceDocumentId));

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

        EvidenceRoomId = evidenceRoomId.Value;

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

        CanRecordCustody =
            (await _authorization.CheckAsync(EmcPermissions.RecordCustodyEvent, evidenceRoomId))
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
        Users = Supervisors;

        if (Custody.OccurredAtLocal == default)
        {
            Custody.OccurredAtLocal = (await _time.NowInRoomAsync(EvidenceRoomId)).DateTime;
        }

        CorrectableFieldsByEvent = View.History
            .Where(r => r.Kind != ItemEventKind.Correction)
            .ToDictionary(r => r.EventId, r => (IReadOnlyCollection<string>)r.EffectiveFields.Keys.ToList());

        LocationReferenceFieldNames = View.History
            .Where(r => r.ReferenceFieldKinds is not null)
            .SelectMany(r => r.ReferenceFieldKinds!)
            .Where(f => f.Value == CorrectableFieldReference.StorageLocation)
            .Select(f => f.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (Location.OccurredAtLocal == default)
        {
            // The room's wall clock now, from the application clock - not the host's.
            Location.OccurredAtLocal = (await _time.NowInRoomAsync(EvidenceRoomId)).DateTime;
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

        /// <summary>Only for a time in the hour repeated when clocks fall back (AUD-020).</summary>
        public AmbiguousLocalTimeChoice AmbiguousTimeChoice { get; set; }

        [StringLength(1000)]
        public string? Reason { get; set; }

        [StringLength(4000)]
        public string? Notes { get; set; }
    }

    /// <summary>A custody row the paper shows (REC-010). Parties are named here, never taken from a reading.</summary>
    public sealed class CustodyInput
    {
        public CustodyPartyKind ReleasedByKind { get; set; } = CustodyPartyKind.InternalUser;
        public int? ReleasedByUserId { get; set; }
        [StringLength(512)] public string? ReleasedByName { get; set; }
        [StringLength(128)] public string? ReleasedByTitleOrGrade { get; set; }
        [StringLength(256)] public string? ReleasedByOrganization { get; set; }
        [StringLength(128)] public string? ReleasedByMailNumber { get; set; }

        public CustodyPartyKind ReceivedByKind { get; set; } = CustodyPartyKind.ExternalPerson;
        public int? ReceivedByUserId { get; set; }
        [StringLength(512)] public string? ReceivedByName { get; set; }
        [StringLength(128)] public string? ReceivedByTitleOrGrade { get; set; }
        [StringLength(256)] public string? ReceivedByOrganization { get; set; }
        [StringLength(128)] public string? ReceivedByMailNumber { get; set; }

        [Required(ErrorMessage = "State the purpose (the Purpose of Change of Custody column).")]
        [StringLength(1000)]
        public string? Purpose { get; set; }

        public DateTime OccurredAtLocal { get; set; }
        public AmbiguousLocalTimeChoice AmbiguousTimeChoice { get; set; }
        public bool IsScrcni { get; set; }
        [StringLength(512)] public string? Destination { get; set; }
        [StringLength(256)] public string? Agency { get; set; }
        [StringLength(4000)] public string? Notes { get; set; }
        public int? SourceDocumentId { get; set; }
        public int? ReconciliationFindingId { get; set; }
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

        /// <summary>The scanned document this correction was reconciled from, if any (REC-004).</summary>
        public int? SourceDocumentId { get; set; }

        /// <summary>AR 195-5 para 1-7c(3) - the corrective action, documented (AUD-004).</summary>
        [Required(ErrorMessage = "A reason is required (AR 195-5 para 1-7c(3)).")]
        [StringLength(2000)]
        public string? Reason { get; set; }

        /// <summary>AR 195-5 para 1-7c(3) - the MFR filed with the DA Form 4137 (AUD-005).</summary>
        [StringLength(256)]
        public string? MfrReference { get; set; }

        /// <summary>
        /// AR 195-5 para 1-7c(3) - the supervisor informed immediately (AUD-005). Either an EMC
        /// user, or - because the responsible CI supervisor frequently holds no EMC account - a
        /// printed name with grade and organization, as the MFR names them (AUD-018).
        /// </summary>
        public int? SupervisorNotifiedUserId { get; set; }

        [StringLength(256)]
        public string? SupervisorNotifiedName { get; set; }

        [StringLength(64)]
        public string? SupervisorNotifiedGradeOrTitle { get; set; }

        [StringLength(256)]
        public string? SupervisorNotifiedOrganization { get; set; }

        /// <summary>
        /// The replacement storage location, for a correction to a field that names one. The
        /// display text is then read from that row by the server, so what the record says and
        /// what it points at cannot disagree (AUD-016).
        /// </summary>
        public int? CorrectedStorageLocationId { get; set; }
    }
}
