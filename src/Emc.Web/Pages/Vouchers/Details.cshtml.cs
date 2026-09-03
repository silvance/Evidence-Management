using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Reads;
using Emc.Domain.Common;
using Emc.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Emc.Web.Pages.Vouchers;

/// <summary>
/// The voucher working page: items while the voucher is a draft, submission for custodian
/// intake, and the custodian's transcription of the official document number.
/// </summary>
public class DetailsModel : PageModel
{
    private const string SuccessKey = "Success";
    private const string WarningsKey = "Warnings";

    private readonly IEvidenceReadService _reads;
    private readonly IVoucherService _vouchers;
    private readonly IEvidenceIntakeService _intake;
    private readonly IEmcPageAuthorization _authorization;

    public DetailsModel(
        IEvidenceReadService reads,
        IVoucherService vouchers,
        IEvidenceIntakeService intake,
        IEmcPageAuthorization authorization)
    {
        _reads = reads;
        _vouchers = vouchers;
        _intake = intake;
        _authorization = authorization;
    }

    public int VoucherId { get; private set; }
    public int CaseId { get; private set; }
    public string CaseControlNumber { get; private set; } = string.Empty;
    public string? RequestingOfficeCaseNumber { get; private set; }
    public string DisplayIdentifier { get; private set; } = string.Empty;
    public string TemporaryIdentifier { get; private set; } = string.Empty;
    public bool HasOfficialDocumentNumber { get; private set; }
    public bool IsSubmitted { get; private set; }
    public VoucherDerivedStatus DerivedStatus { get; private set; }
    public string ReceivingActivity { get; private set; } = string.Empty;
    public string ReceivingActivityLocation { get; private set; } = string.Empty;
    public string ReceivedFrom { get; private set; } = string.Empty;
    public DateTimeOffset AcquiredAtLocal { get; private set; }

    public IReadOnlyList<ItemListRow> Items { get; private set; } = [];
    public IReadOnlyList<DocumentNumberRow> DocumentNumbers { get; private set; } = [];

    public bool CanEditDraft { get; private set; }
    public bool CanSubmit { get; private set; }
    public bool CanRecordDocumentNumber { get; private set; }

    /// <summary>
    /// IAM-006 - an alternate custodian past the AR 195-5 para 1-4i window is warned, not blocked
    /// (open decision DEC-05). The warning must reach the screen, not only the log.
    /// </summary>
    public IReadOnlyList<string> AuthorizationWarnings { get; private set; } = [];

    public PageMessages Messages { get; } = new();

    [BindProperty]
    public NewItemInput NewItem { get; set; } = new();

    [BindProperty]
    public DocumentNumberInput DocumentNumber { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAddItemAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(NewItem)))
        {
            return Page();
        }

        var result = await _vouchers.AddItemAsync(new AddItemRequest(
            VoucherId: id,
            Description: NewItem.Description!,
            Quantity: NewItem.Quantity,
            SerialNumber: NewItem.SerialNumber,
            UniqueDeviceIdentifier: NewItem.UniqueDeviceIdentifier,
            IsPossibleBiohazard: NewItem.IsPossibleBiohazard,
            IsFungible: NewItem.IsFungible,
            IsSealed: NewItem.IsSealed,
            SealDescription: NewItem.SealDescription));

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings, "Item added to the draft voucher.");
    }

    public async Task<IActionResult> OnPostRemoveItemAsync(int id, int itemId)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var result = await _vouchers.RemoveItemAsync(itemId);

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings, "Item removed. The remaining items have been renumbered.");
    }

    public async Task<IActionResult> OnPostSubmitAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var result = await _vouchers.SubmitForCustodianIntakeAsync(id);

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings, "Voucher submitted for evidence custodian intake.");
    }

    public async Task<IActionResult> OnPostRecordDocumentNumberAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(DocumentNumber)))
        {
            return Page();
        }

        // EMC-002 / VCH-006. AR 195-5 para 2-4c assigns the number by order of precedence from
        // the evidence ledger, and para 2-5a keeps that ledger a bound book absent approval under
        // para 2-5c. The custodian's confirmation is an explicit, recorded act - never inferred
        // from the fact that a number was typed into a box.
        if (!DocumentNumber.AttestedAssignedInAuthoritativeLedger)
        {
            Messages.Error =
                "AR 195-5 para 2-4c: confirm that this document number was assigned by order of "
                + "precedence in the authoritative evidence ledger before recording it here. This "
                + "application operates as a companion under para 2-5c and does not assign "
                + "document numbers.";
            Messages.RequirementId = "EMC-002";
            return Page();
        }

        var receivedAtLocal = new DateTimeOffset(
            DocumentNumber.ReceivedAtLocal,
            TimeZoneInfo.Local.GetUtcOffset(DocumentNumber.ReceivedAtLocal));

        var result = await _intake.RecordOfficialDocumentNumberAsync(new RecordDocumentNumberRequest(
            VoucherId: id,
            DocumentNumber: DocumentNumber.Value!,
            AttestedAssignedInAuthoritativeLedger: true,
            ReceivedAtLocal: receivedAtLocal,
            SupersessionReason: DocumentNumber.SupersessionReason));

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings,
            $"Evidence document number {DocumentNumber.Value} recorded. The items on this voucher "
            + "are now accounted for in the evidence room.");
    }

    private IActionResult Respond(
        int id,
        bool succeeded,
        string? error,
        string? requirementId,
        IReadOnlyList<string> warnings,
        string successMessage)
    {
        if (!succeeded)
        {
            Messages.Error = error;
            Messages.RequirementId = requirementId;
            return Page();
        }

        // Warnings survive the redirect, so an advisory - a document-number gap (VCH-009), a
        // possible supposition phrase (ITEM-003) - is not lost across POST/redirect/GET.
        TempData[SuccessKey] = successMessage;

        if (warnings.Count > 0)
        {
            TempData[WarningsKey] = JsonSerializer.Serialize(warnings);
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
        // Authorizes before returning anything; null when the caller may not read this voucher,
        // which the page turns into a 404 so identifiers cannot be enumerated (IAM-018).
        var view = await _reads.GetVoucherAsync(id);
        if (view is null)
        {
            return false;
        }

        VoucherId = view.Id;
        CaseId = view.CaseId;
        CaseControlNumber = view.CaseControlNumber;
        RequestingOfficeCaseNumber = view.RequestingOfficeCaseNumber;
        DisplayIdentifier = view.DisplayIdentifier;
        TemporaryIdentifier = view.TemporaryIdentifier;
        HasOfficialDocumentNumber = view.HasOfficialDocumentNumber;
        IsSubmitted = view.IsSubmitted;
        DerivedStatus = view.DerivedStatus;
        ReceivingActivity = view.ReceivingActivity;
        ReceivingActivityLocation = view.ReceivingActivityLocation;
        ReceivedFrom = view.ReceivedFrom;
        AcquiredAtLocal = view.AcquiredAtLocal;
        Items = view.Items;
        DocumentNumbers = view.DocumentNumbers;

        var editDecision = await _authorization.CheckAsync(
            EmcPermissions.EditDraftVoucher, view.EvidenceRoomId);

        var numberDecision = await _authorization.CheckAsync(
            EmcPermissions.RecordOfficialDocumentNumber, view.EvidenceRoomId);

        CanEditDraft = editDecision.IsAllowed && view.AllowsItemEditing;
        CanSubmit = editDecision.IsAllowed && view.AllowsItemEditing && view.Items.Count > 0;
        CanRecordDocumentNumber = numberDecision.IsAllowed && view.IsSubmitted;
        AuthorizationWarnings = numberDecision.Warnings ?? [];

        if (TempData[SuccessKey] is string success)
        {
            Messages.Success = success;
        }

        if (TempData[WarningsKey] is string packed && packed.Length > 0)
        {
            Messages.Warnings = JsonSerializer.Deserialize<List<string>>(packed) ?? [];
        }

        if (DocumentNumber.ReceivedAtLocal == default)
        {
            DocumentNumber.ReceivedAtLocal = DateTime.Now;
        }

        return true;
    }

    public sealed class NewItemInput
    {
        /// <summary>AR 195-5 para 2-3d - the Description of Articles block (ITEM-003).</summary>
        [Required(ErrorMessage = "A description is required (AR 195-5 para 2-3d).")]
        [StringLength(4000)]
        public string? Description { get; set; }

        [StringLength(256)]
        public string? Quantity { get; set; }

        [StringLength(256)]
        public string? SerialNumber { get; set; }

        [StringLength(256)]
        public string? UniqueDeviceIdentifier { get; set; }

        public bool IsPossibleBiohazard { get; set; }
        public bool IsFungible { get; set; }
        public bool IsSealed { get; set; }

        [StringLength(1000)]
        public string? SealDescription { get; set; }
    }

    public sealed class DocumentNumberInput
    {
        /// <summary>AR 195-5 para 2-4c - NNN-YY (VCH-004).</summary>
        [Required(ErrorMessage = "The evidence document number is required.")]
        [RegularExpression(
            @"^\d{3}-\d{2}$",
            ErrorMessage = "AR 195-5 para 2-4c: three digits, a hyphen, then the two-digit calendar year (for example 037-26).")]
        public string? Value { get; set; }

        public DateTime ReceivedAtLocal { get; set; }

        /// <summary>EMC-002 / VCH-006 - an explicit, stored custodian attestation.</summary>
        public bool AttestedAssignedInAuthoritativeLedger { get; set; }

        /// <summary>AR 195-5 para 2-7g - required when superseding an existing number (VCH-008).</summary>
        [StringLength(1000)]
        public string? SupersessionReason { get; set; }
    }
}
