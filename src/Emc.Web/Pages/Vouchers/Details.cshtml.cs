using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Emc.Application.Authorization;
using Emc.Application.Cases;
using Emc.Application.Reads;
using Emc.Domain.Cases;
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

    /// <summary>How this room writes document numbers (VCH-023), for the form.</summary>
    public string DocumentNumberFormatDescription { get; private set; } = string.Empty;
    public string DocumentNumberExample { get; private set; } = string.Empty;
    public bool DocumentNumberLayoutIsRegulatory { get; private set; } = true;
    public bool DocumentNumberLayoutAwaitsValidation { get; private set; }

    public bool CanEditDraft { get; private set; }
    public bool CanSubmit { get; private set; }
    public bool CanRecordDocumentNumber { get; private set; }

    /// <summary>AR 195-5 2-3g - the custodian's pre-acceptance review of the form.</summary>
    public VoucherReviewStage ReviewStage { get; private set; }
    public IReadOnlyList<VoucherReviewActionRow> ReviewActions { get; private set; } = [];
    public bool CanReturnForCorrection { get; private set; }
    public bool CanRecordAgentCorrection { get; private set; }
    public bool CanResubmit { get; private set; }

    /// <summary>
    /// Advisories attached to an ALLOWED decision - most importantly the LOCAL notice that an
    /// alternate custodian is within a few days of the end of the AR 195-5 para 1-4i
    /// temporary-absence window, so the para 3-2d transition can be started before authority
    /// lapses. Past the window the alternate is DENIED, not warned (IAM-020, DEC-05); these are
    /// advance notice only. They must reach the screen, not only the log.
    /// </summary>
    public IReadOnlyList<string> AuthorizationWarnings { get; private set; } = [];

    public PageMessages Messages { get; } = new();

    [BindProperty]
    public NewItemInput NewItem { get; set; } = new();

    [BindProperty]
    public DocumentNumberInput DocumentNumber { get; set; } = new();

    [BindProperty]
    public ReturnInput Return { get; set; } = new();

    [BindProperty]
    public AgentCorrectionInput AgentCorrection { get; set; } = new();

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

    public async Task<IActionResult> OnPostReturnForCorrectionAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(Return)))
        {
            return Page();
        }

        var result = await _vouchers.ReturnForCorrectionAsync(
            new ReturnVoucherForCorrectionRequest(id, Return.ErrorsIdentified!));

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings, "Voucher returned to the submitting agent for correction (AR 195-5 para 2-3g).");
    }

    public async Task<IActionResult> OnPostRecordAgentCorrectionAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!IsValidForPrefix(nameof(AgentCorrection)))
        {
            return Page();
        }

        var result = await _vouchers.RecordAgentCorrectionAsync(new RecordAgentCorrectionRequest(
            id, AgentCorrection.WhatWasCorrected!, AgentCorrection.PaperFormCorrectedAndInitialedAttested));

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings, "Correction recorded. Resubmit the voucher when the form is ready for the custodian.");
    }

    public async Task<IActionResult> OnPostResubmitAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var result = await _vouchers.ResubmitForCustodianIntakeAsync(id);

        return Respond(id, result.Succeeded, result.Error, result.RequirementId,
            result.Warnings, "Corrected voucher resubmitted for evidence custodian intake.");
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
            SupersessionReason: DocumentNumber.SupersessionReason,
            ConfirmedCalendarYear: DocumentNumber.ConfirmedCalendarYear));

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
        DocumentNumberFormatDescription = view.DocumentNumberFormatDescription ?? string.Empty;
        DocumentNumberExample = view.DocumentNumberExample ?? string.Empty;
        DocumentNumberLayoutIsRegulatory = view.DocumentNumberLayoutIsRegulatory;
        DocumentNumberLayoutAwaitsValidation = view.DocumentNumberLayoutAwaitsValidation;

        var editDecision = await _authorization.CheckAsync(
            EmcPermissions.EditDraftVoucher, view.EvidenceRoomId);

        var numberDecision = await _authorization.CheckAsync(
            EmcPermissions.RecordOfficialDocumentNumber, view.EvidenceRoomId);

        var returnDecision = await _authorization.CheckAsync(
            EmcPermissions.ReturnVoucherForCorrection, view.EvidenceRoomId);

        ReviewStage = view.ReviewStage;
        ReviewActions = view.ReviewActions ?? [];

        // These decide what the page OFFERS. Every one of them is enforced again server-side
        // in the service and the domain, which is where "only the submitting agent" (2-3g) is
        // checked; the page does not know who that is beyond a hint.
        CanEditDraft = editDecision.IsAllowed && view.AllowsItemEditing;
        CanSubmit = editDecision.IsAllowed && view.ReviewStage == VoucherReviewStage.Draft && view.Items.Count > 0;
        CanRecordDocumentNumber = numberDecision.IsAllowed && view.IsSubmitted;
        CanReturnForCorrection = returnDecision.IsAllowed
            && !view.HasOfficialDocumentNumber
            && view.ReviewStage is VoucherReviewStage.SubmittedForCustodianReview
                or VoucherReviewStage.ResubmittedForCustodianReview;
        CanRecordAgentCorrection = editDecision.IsAllowed
            && view.ReviewStage == VoucherReviewStage.ReturnedToSubmittingAgentForCorrection;
        CanResubmit = editDecision.IsAllowed
            && view.ReviewStage == VoucherReviewStage.CorrectedBySubmittingAgent;
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

    public sealed class ReturnInput
    {
        /// <summary>AR 195-5 para 2-3g - what the custodian identified for correction (VCH-017).</summary>
        [Required(ErrorMessage = "State the errors identified (AR 195-5 para 2-3g).")]
        [StringLength(4000)]
        public string? ErrorsIdentified { get; set; }
    }

    public sealed class AgentCorrectionInput
    {
        /// <summary>AR 195-5 para 2-3g - what the submitting agent corrected (VCH-018).</summary>
        [Required(ErrorMessage = "State what was corrected.")]
        [StringLength(4000)]
        public string? WhatWasCorrected { get; set; }

        /// <summary>
        /// The agent's attestation that the PAPER DA Form 4137 was corrected and initialed
        /// (VCH-019). An attestation, not an initial; the application supplies neither.
        /// </summary>
        public bool PaperFormCorrectedAndInitialedAttested { get; set; }
    }

    public sealed class DocumentNumberInput
    {
        /// <summary>
        /// The number as the custodian wrote it in the ledger (VCH-004). No layout is hard-coded
        /// here: the room's numbering policy decides the layout, and the service validates the
        /// entry against it and returns the room's own description on failure (VCH-023).
        /// </summary>
        [Required(ErrorMessage = "The evidence document number is required.")]
        [StringLength(24)]
        public string? Value { get; set; }

        /// <summary>
        /// The four-digit calendar year, entered only when the digits written do not match the
        /// year of receipt. The application never guesses the century (VCH-022).
        /// </summary>
        [Range(1990, 2199)]
        public int? ConfirmedCalendarYear { get; set; }

        public DateTime ReceivedAtLocal { get; set; }

        /// <summary>EMC-002 / VCH-006 - an explicit, stored custodian attestation.</summary>
        public bool AttestedAssignedInAuthoritativeLedger { get; set; }

        /// <summary>AR 195-5 para 2-7g - required when superseding an existing number (VCH-008).</summary>
        [StringLength(1000)]
        public string? SupersessionReason { get; set; }
    }
}
