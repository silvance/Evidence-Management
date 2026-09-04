using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Emc.Application.Abstractions;
using Emc.Application.Authorization;
using Emc.Application.Filing;
using Emc.Application.Suspense;
using Emc.Application.Time;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Emc.Domain.Suspense;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Emc.Web.Pages.Releases;

/// <summary>
/// One temporary release (AR 195-5 2-7a, 2-7b): what went, to whom, since when; the contact
/// history (2-7a); the return of items (2-7b, 2-7d, 2-7e) with the location only as the
/// custodian says (LOC-008); and items accounted for without return (3-1a(4), 2-7c(2)).
/// </summary>
public class DetailsModel : PageModel
{
    private readonly ITemporaryReleaseService _releases;
    private readonly IPhysicalDocumentService _physical;
    private readonly IEmcPageAuthorization _authorization;
    private readonly IEvidenceRoomTimeService _time;
    private readonly IEmcDbContext _db;

    public DetailsModel(ITemporaryReleaseService releases, IPhysicalDocumentService physical, IEmcPageAuthorization authorization, IEvidenceRoomTimeService time, IEmcDbContext db)
    {
        _releases = releases;
        _physical = physical;
        _authorization = authorization;
        _time = time;
        _db = db;
    }

    public TemporaryReleaseView? View { get; private set; }
    public bool CanRecordContact { get; private set; }
    public bool CanReturn { get; private set; }
    public IReadOnlyList<(int Id, string Path)> Locations { get; private set; } = [];
    public IReadOnlyList<FileContainerRow> ActiveFiles { get; private set; } = [];
    public PageMessages Messages { get; } = new();

    [BindProperty]
    public ContactInput Contact { get; set; } = new();

    [BindProperty]
    public ReturnInput Return { get; set; } = new();

    [BindProperty]
    public NotReturnedInput NotReturned { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostContactAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(Contact)))
        {
            return Page();
        }

        var contacted = await _time.ResolveLocalAsync(View!.EvidenceRoomId, Contact.ContactedAtLocal, Contact.AmbiguousTimeChoice);
        if (!contacted.Succeeded)
        {
            return Fail(contacted.Error, contacted.RequirementId);
        }

        DateTimeOffset? next = null;
        if (Contact.NextFollowUpLocal is { } n)
        {
            var resolved = await _time.ResolveLocalAsync(View.EvidenceRoomId, n);
            if (!resolved.Succeeded)
            {
                return Fail(resolved.Error, resolved.RequirementId);
            }

            next = resolved.Value;
        }

        var result = await _releases.RecordContactAsync(new RecordSuspenseContactRequest(id, contacted.Value!.Value, Contact.Method, Contact.ContactedPerson ?? string.Empty, Contact.Outcome, Contact.Narrative, next));
        return Respond(id, result.Succeeded, result.Error, result.RequirementId, result.Warnings, "Contact recorded (AR 195-5 para 2-7a).");
    }

    public async Task<IActionResult> OnPostReturnAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(Return)))
        {
            return Page();
        }

        var returned = await _time.ResolveLocalAsync(View!.EvidenceRoomId, Return.ReturnedAtLocal, Return.AmbiguousTimeChoice);
        if (!returned.Succeeded)
        {
            return Fail(returned.Error, returned.RequirementId);
        }

        var items = new List<ReturnedItem>();
        foreach (var itemId in Return.ItemIds ?? [])
        {
            Return.Locations.TryGetValue(itemId, out var locationId);
            Return.ConfirmPrior.TryGetValue(itemId, out var confirm);
            Return.ApparentChange.TryGetValue(itemId, out var annotation);
            Return.ApparentChangeMfr.TryGetValue(itemId, out var mfr);
            var change = string.IsNullOrWhiteSpace(annotation) && string.IsNullOrWhiteSpace(mfr) ? null : new ControlledSubstanceApparentChange(annotation ?? string.Empty, mfr ?? string.Empty);
            items.Add(new ReturnedItem(itemId, locationId > 0 ? locationId : null, confirm, change));
        }

        ReleaseRecipient? returnedBy = string.IsNullOrWhiteSpace(Return.ReturnMailNumber)
            ? null
            : new ReleaseRecipient(CustodyPartyKind.AccountableMailNumber, Return.ReturnMailNumber, AccountableMailNumber: Return.ReturnMailNumber, Carrier: Return.ReturnCarrier);

        var result = await _releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(
            id, items, returned.Value!.Value, Return.OriginalAnnotatedByCustodianAndReturnerAttested, Return.FirstCopyChainAnnotatedAttested,
            returnedBy, Return.ActiveFileContainerId > 0 ? Return.ActiveFileContainerId : null, null, Return.Notes));
        return Respond(id, result.Succeeded, result.Error, result.RequirementId, result.Warnings, "Return recorded (AR 195-5 para 2-7b): custody, state, location as stated, and the paper.");
    }

    public async Task<IActionResult> OnPostNotReturnedAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(NotReturned)))
        {
            return Page();
        }

        var occurred = await _time.ResolveLocalAsync(View!.EvidenceRoomId, NotReturned.OccurredAtLocal, NotReturned.AmbiguousTimeChoice);
        if (!occurred.Succeeded)
        {
            return Fail(occurred.Error, occurred.RequirementId);
        }

        var result = await _releases.RecordNotReturnedAsync(new NotReturnedRequest(id, NotReturned.ItemIds ?? [], NotReturned.Reason, occurred.Value!.Value, NotReturned.Narrative ?? string.Empty, NotReturned.MfrReference));
        return Respond(id, result.Succeeded, result.Error, result.RequirementId, result.Warnings, "Recorded: the item(s) are accounted for without return and are now pending disposition.");
    }

    private IActionResult Fail(string? error, string? requirementId)
    {
        Messages.Error = error;
        Messages.RequirementId = requirementId;
        return Page();
    }

    private IActionResult Respond(int id, bool succeeded, string? error, string? requirementId, IReadOnlyList<string> warnings, string success)
    {
        if (!succeeded)
        {
            return Fail(error, requirementId);
        }

        TempData["Success"] = success;
        if (warnings.Count > 0)
        {
            TempData["Warnings"] = JsonSerializer.Serialize(warnings);
        }

        return RedirectToPage(new { id });
    }

    private bool IsValidForPrefix(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            ModelState.Remove(key);
        }

        return ModelState.IsValid;
    }

    private async Task<bool> LoadAsync(int id)
    {
        View = await _releases.GetAsync(id);
        if (View is null)
        {
            return false;
        }

        CanRecordContact = View.Status == TemporaryReleaseStatus.Open && (await _authorization.CheckAsync(EmcPermissions.ReleaseTemporarily, View.EvidenceRoomId)).IsAllowed;
        CanReturn = View.Status == TemporaryReleaseStatus.Open && (await _authorization.CheckAsync(EmcPermissions.ReturnFromTemporaryRelease, View.EvidenceRoomId)).IsAllowed;

        var locations = await _db.StorageLocations.AsNoTracking().Include(l => l.Parent).Where(l => l.EvidenceRoomId == View.EvidenceRoomId && l.IsActive).ToListAsync();
        Locations = locations.Select(l => (l.Id, l.FullPath)).OrderBy(l => l.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
        ActiveFiles = (await _physical.GetContainersAsync(View.EvidenceRoomId)).Where(c => c.IsActive && c.Kind == PhysicalFileKind.Active4137File).ToList();

        var now = (await _time.NowInRoomAsync(View.EvidenceRoomId)).DateTime;
        if (Contact.ContactedAtLocal == default) Contact.ContactedAtLocal = now;
        if (Return.ReturnedAtLocal == default) Return.ReturnedAtLocal = now;
        if (NotReturned.OccurredAtLocal == default) NotReturned.OccurredAtLocal = now;

        if (TempData["Success"] is string success)
        {
            Messages.Success = success;
        }

        if (TempData["Warnings"] is string packed && packed.Length > 0)
        {
            Messages.Warnings = JsonSerializer.Deserialize<List<string>>(packed) ?? [];
        }

        return true;
    }

    public sealed class ContactInput
    {
        public DateTime ContactedAtLocal { get; set; }
        public AmbiguousLocalTimeChoice AmbiguousTimeChoice { get; set; }
        public ContactMethod Method { get; set; } = ContactMethod.Telephone;

        [Required(ErrorMessage = "Name the person or office contacted.")]
        [StringLength(256)]
        public string? ContactedPerson { get; set; }

        public ContactOutcome Outcome { get; set; } = ContactOutcome.EvidenceStillRequired;

        [StringLength(2000)]
        public string? Narrative { get; set; }

        public DateTime? NextFollowUpLocal { get; set; }
    }

    public sealed class ReturnInput
    {
        public List<int>? ItemIds { get; set; }
        public Dictionary<int, int> Locations { get; set; } = new();
        public Dictionary<int, bool> ConfirmPrior { get; set; } = new();
        public Dictionary<int, string?> ApparentChange { get; set; } = new();
        public Dictionary<int, string?> ApparentChangeMfr { get; set; } = new();
        public DateTime ReturnedAtLocal { get; set; }
        public AmbiguousLocalTimeChoice AmbiguousTimeChoice { get; set; }
        public bool OriginalAnnotatedByCustodianAndReturnerAttested { get; set; }
        public bool FirstCopyChainAnnotatedAttested { get; set; }
        public int ActiveFileContainerId { get; set; }

        [StringLength(128)]
        public string? ReturnMailNumber { get; set; }

        [StringLength(128)]
        public string? ReturnCarrier { get; set; }

        [StringLength(2000)]
        public string? Notes { get; set; }
    }

    public sealed class NotReturnedInput
    {
        public List<int>? ItemIds { get; set; }
        public NotReturnedReason Reason { get; set; } = NotReturnedReason.EnteredInRecordOfTrial;
        public DateTime OccurredAtLocal { get; set; }
        public AmbiguousLocalTimeChoice AmbiguousTimeChoice { get; set; }

        [Required(ErrorMessage = "Say what became of the item(s).")]
        [StringLength(2000)]
        public string? Narrative { get; set; }

        [StringLength(256)]
        public string? MfrReference { get; set; }
    }
}
