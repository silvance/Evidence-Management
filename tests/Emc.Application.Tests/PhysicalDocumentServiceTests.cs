using Emc.Application.Cases;
using Emc.Application.Filing;
using Emc.Domain.Common;
using Emc.Domain.Filing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>The paper DA Form 4137 record through the service: authorization, the 50-voucher limit at the store, 2-4h gating.</summary>
public class PhysicalDocumentServiceTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private IPhysicalDocumentService Service()
        => new PhysicalDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock);

    private async Task<int> AcceptedVoucherAsync(string number)
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            $"CASE-{Guid.NewGuid():N}"[..20], "Paper test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD", "SUBJECT residence", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "One item", "1", null, null, false, false, false, null));
        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucher.Value);
        _harness.SignInAsCustodian();
        var numbered = await _harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucher.Value, number, true, _harness.Clock.UtcNow));
        Assert.True(numbered.Succeeded, numbered.Error);
        return voucher.Value;
    }

    private async Task<int> ActiveFileAsync(string label = "ACTIVE 001-26 to 050-26")
    {
        _harness.SignInAsCustodian();
        var result = await Service().CreateContainerAsync(new CreateFileContainerRequest(
            _harness.EvidenceRoomId, PhysicalFileKind.Active4137File, ContainerForm.Binder, label, "001-26", "050-26"));
        Assert.True(result.Succeeded, result.Error);
        return result.Value;
    }

    [Fact]
    public async Task TheCustodianFilesTheOriginal_AndAnAgentCannot()
    {
        var voucherId = await AcceptedVoucherAsync("001-26");
        var fileId = await ActiveFileAsync();

        _harness.SignInAsAgent();
        var denied = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.False(denied.Succeeded);

        _harness.SignInAsUnappointedCustodian();
        var unappointed = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.False(unappointed.Succeeded);
        Assert.Equal("IAM-005", unappointed.RequirementId);

        _harness.SignInAsCustodian();
        var filed = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId, "Filed after numbering"));
        Assert.True(filed.Succeeded, filed.Error);

        var view = await Service().GetForVoucherAsync(voucherId);
        Assert.Equal(PhysicalOriginalStatus.FiledActive, view!.OriginalStatus);
        Assert.Equal("ACTIVE 001-26 to 050-26", view.OriginalContainerLabel);
        Assert.Equal(PaperRetentionStatus.Retain, view.RetentionStatus);
    }

    [Fact]
    public async Task AnUnnumberedVoucherHasNoOriginalToFile()
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest("0500-2026-CID902-XXXXX", "Paper", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD", "SUBJECT residence", _harness.Clock.UtcNow, false, null));
        var fileId = await ActiveFileAsync();

        var result = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            voucher.Value, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));

        Assert.False(result.Succeeded);
        Assert.Equal("FIL-004", result.RequirementId);
    }

    [Fact]
    public async Task TheFiftyVoucherLimitIsEnforcedFromTheStoredCount()
    {
        // FIL-002 [REG] 2-4f(1), with the count coming from the database, not from memory.
        var fileId = await ActiveFileAsync();

        // Fifty paper records already in the binder, written directly.
        for (var i = 0; i < 50; i++)
        {
            var voucherId = await AcceptedVoucherAsync($"{100 + i:D3}-26");
            var document = new PhysicalVoucherDocument(voucherId, _harness.EvidenceRoomId);
            var container = await _harness.Db.PhysicalFileContainers.FirstAsync(c => c.Id == fileId);
            document.FileOriginalInActiveFile(container, i, _harness.CustodianUserId, _harness.Clock.UtcNow);
            _harness.Db.PhysicalVoucherDocuments.Add(document);
        }

        await _harness.Db.SaveChangesAsync();

        var fiftyFirst = await AcceptedVoucherAsync("200-26");
        var result = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            fiftyFirst, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));

        Assert.False(result.Succeeded);
        Assert.Equal("FIL-002", result.RequirementId);

        var containers = await Service().GetContainersAsync(_harness.EvidenceRoomId);
        Assert.Equal(50, containers.Single(c => c.Id == fileId).VouchersFiled);
    }

    [Fact]
    public async Task InactiveFilingRequiresEveryItemDisposed()
    {
        // FIL-006 [REG] 2-4h. The item is InEvidenceRoom, so the form is not inactive.
        var voucherId = await AcceptedVoucherAsync("003-26");
        _harness.SignInAsCustodian();
        var inactive = await Service().CreateContainerAsync(new CreateFileContainerRequest(
            _harness.EvidenceRoomId, PhysicalFileKind.Inactive4137File, ContainerForm.Folder, "INACTIVE SEP 2026", DispositionYear: 2026, DispositionMonth: 9));

        var result = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            voucherId, PhysicalDocumentAction.FileOriginalInactive, _harness.Clock.UtcNow, inactive.Value));

        Assert.False(result.Succeeded);
        Assert.Equal("FIL-006", result.RequirementId);
        Assert.Contains("ALL items", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRoundTripReleaseReturnInactiveEligibility()
    {
        var voucherId = await AcceptedVoucherAsync("004-26");
        var fileId = await ActiveFileAsync();
        _harness.SignInAsCustodian();
        var usacil = (await Service().CreateContainerAsync(new CreateFileContainerRequest(
            _harness.EvidenceRoomId, PhysicalFileKind.SuspenseUsacil, ContainerForm.Folder, "USACIL"))).Value;
        var inactive = (await Service().CreateContainerAsync(new CreateFileContainerRequest(
            _harness.EvidenceRoomId, PhysicalFileKind.Inactive4137File, ContainerForm.Folder, "INACTIVE OCT 2026", DispositionYear: 2026, DispositionMonth: 10))).Value;

        async Task Ok(PhysicalDocumentAction action, int? container, string? narrative = null)
        {
            var r = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, action, _harness.Clock.UtcNow, container, narrative));
            Assert.True(r.Succeeded, $"{action}: {r.Error}");
        }

        await Ok(PhysicalDocumentAction.FileOriginalInActiveFile, fileId);
        _harness.Clock.Advance(TimeSpan.FromDays(1));
        await Ok(PhysicalDocumentAction.ReleaseOriginalWithEvidence, usacil, "To USACIL");
        _harness.Clock.Advance(TimeSpan.FromDays(20));
        await Ok(PhysicalDocumentAction.ReturnOriginalToActiveFile, fileId);

        // Dispose the item through its own state machine so 2-4h's condition holds.
        var item = await _harness.Db.EvidenceItems.FirstAsync(i => i.VoucherId == voucherId);
        item.TransitionTo(AccountabilityStatus.DispositionPending);
        item.TransitionTo(AccountabilityStatus.Disposed);
        await _harness.Db.SaveChangesAsync();

        _harness.Clock.Advance(TimeSpan.FromDays(10));
        var inactiveAt = _harness.Clock.UtcNow;
        await Ok(PhysicalDocumentAction.FileOriginalInactive, inactive, "All items disposed");

        var view = await Service().GetForVoucherAsync(voucherId);
        Assert.Equal(PhysicalOriginalStatus.FiledInactive, view!.OriginalStatus);
        Assert.Equal(inactiveAt, view.InactiveSinceUtc);
        Assert.Equal(inactiveAt.AddYears(3), view.DestructionEligibleAtUtc);
        Assert.Equal(PaperRetentionStatus.Retain, view.RetentionStatus);

        // Early destruction refused; eligible after three years; confirmed by the custodian.
        var early = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.ConfirmDestruction, _harness.Clock.UtcNow, null, "shredded"));
        Assert.Equal("FIL-009", early.RequirementId);

        _harness.Clock.Advance(TimeSpan.FromDays(365 * 3 + 2));
        Assert.Equal(PaperRetentionStatus.EligibleForDestruction, (await Service().GetForVoucherAsync(voucherId))!.RetentionStatus);

        await Ok(PhysicalDocumentAction.ConfirmDestruction, null, "Shredded; witnessed by the alternate custodian.");
        Assert.Equal(PaperRetentionStatus.DestructionConfirmed, (await Service().GetForVoucherAsync(voucherId))!.RetentionStatus);

        Assert.Equal(6, (await Service().GetForVoucherAsync(voucherId))!.Events.Count); // filed, accompanies + suspense copy, returned, inactive, destroyed
    }

    [Fact]
    public async Task AContainerFromAnotherRoomIsNotFound()
    {
        var voucherId = await AcceptedVoucherAsync("005-26");
        var other = new PhysicalFileContainer(_harness.OtherEvidenceRoomId, PhysicalFileKind.Active4137File, ContainerForm.Folder, "OTHER ROOM");
        _harness.Db.PhysicalFileContainers.Add(other);
        await _harness.Db.SaveChangesAsync();

        _harness.SignInAsCustodian();
        var result = await Service().RecordAsync(new PhysicalDocumentActionRequest(
            voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, other.Id));

        Assert.False(result.Succeeded);
        Assert.Equal("FIL-001", result.RequirementId);
    }

    [Fact]
    public async Task ReadingThePaperRecordIsRoomScoped()
    {
        var voucherId = await AcceptedVoucherAsync("006-26");
        _harness.SignInAsAdministrator();
        Assert.Null(await Service().GetForVoucherAsync(voucherId));
        Assert.Empty(await Service().GetContainersAsync(_harness.EvidenceRoomId));
    }
}
