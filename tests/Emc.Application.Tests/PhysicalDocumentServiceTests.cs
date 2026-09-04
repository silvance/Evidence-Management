using Emc.Application.Cases;
using Emc.Application.Filing;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>The paper DA Form 4137 record through the service: authority, ranges, counts, concurrency, closure bases.</summary>
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

    private async Task<int> ActiveFileAsync(int from = 1, int to = 50, string? label = null)
    {
        _harness.SignInAsCustodian();
        var result = await Service().CreateContainerAsync(new CreateFileContainerRequest(
            _harness.EvidenceRoomId, PhysicalFileKind.Active4137File, ContainerForm.Binder, label ?? $"ACTIVE {from:000}-26 to {to:000}-26", 2026, from, to));
        Assert.True(result.Succeeded, result.Error);
        return result.Value;
    }

    private async Task<int> FolderAsync(PhysicalFileKind kind, string label, int? year = null, int? month = null)
    {
        _harness.SignInAsCustodian();
        var result = await Service().CreateContainerAsync(new CreateFileContainerRequest(_harness.EvidenceRoomId, kind, ContainerForm.Folder, label, null, null, null, year, month));
        Assert.True(result.Succeeded, result.Error);
        return result.Value;
    }

    private async Task SetItemsAsync(int voucherId, AccountabilityStatus to)
    {
        var items = await _harness.Db.EvidenceItems.Include(i => i.Events).Where(i => i.VoucherId == voucherId).ToListAsync();
        foreach (var item in items)
        {
            var now = _harness.Clock.UtcNow;
            var from = item.AccountabilityStatus;
            item.TransitionTo(to);
            await _harness.EventRecorder.AppendAsync(item, new StatusEvent(from, to, $"Test: {to}", now, now, _harness.CustodianUserId));
        }

        await _harness.Db.SaveChangesAsync();
        _harness.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task TheCustodianFilesTheOriginal_AndAnAgentCannot()
    {
        var voucherId = await AcceptedVoucherAsync("001-26");
        var fileId = await ActiveFileAsync();

        _harness.SignInAsAgent();
        var denied = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.False(denied.Succeeded);

        _harness.SignInAsCustodian();
        var filed = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId, "Filed."));
        Assert.True(filed.Succeeded, filed.Error);

        var view = await Service().GetForVoucherAsync(voucherId);
        Assert.Equal(OriginalDisposition.HeldActive, view!.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.ActiveOriginal, view.RetainedPaperStatus);
        Assert.Equal("ACTIVE 001-26 to 050-26", view.CurrentContainerLabel);
        Assert.Equal("ACTIVE 001-26 to 050-26", view.HomeActiveContainerLabel);
        Assert.Equal(1, (await Service().GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == fileId).VouchersFiled);
    }

    [Fact]
    public async Task TheLabelRangeIsRenderedInTheRoomsLayout_AndTheCanonicalRangeIsWhatIsChecked()
    {
        var fileId = await ActiveFileAsync(1, 50);
        var row = (await Service().GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == fileId);
        Assert.Equal("001-26", row.RangeFrom);
        Assert.Equal("050-26", row.RangeTo);
        Assert.Equal(2026, row.RangeCalendarYear);
        Assert.Equal((1, 50), (row.RangeFromSequence, row.RangeToSequence));

        // FIL-011, FIL-012 through the service.
        var other = await Service().CreateContainerAsync(new CreateFileContainerRequest(_harness.EvidenceRoomId, PhysicalFileKind.Active4137File, ContainerForm.Other, "box", 2026, 51, 100));
        Assert.Equal("FIL-011", other.RequirementId);
        var noRange = await Service().CreateContainerAsync(new CreateFileContainerRequest(_harness.EvidenceRoomId, PhysicalFileKind.Active4137File, ContainerForm.Binder, "unlabelled"));
        Assert.Equal("FIL-012", noRange.RequirementId);
    }

    [Fact]
    public async Task AVoucherIsFiledOnlyInTheBinderWhoseRangeCoversIt()
    {
        // 051-26 cannot go into 001-26 through 050-26.
        var first = await ActiveFileAsync(1, 50);
        var second = await ActiveFileAsync(51, 100);
        var voucher51 = await AcceptedVoucherAsync("051-26");

        var wrong = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucher51, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, first));
        Assert.False(wrong.Succeeded);
        Assert.Equal("FIL-012", wrong.RequirementId);
        Assert.Equal(0, (await Service().GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == first).VouchersFiled);

        var right = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucher51, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, second));
        Assert.True(right.Succeeded, right.Error);
    }

    [Fact]
    public async Task TwoConcurrentFilingsCannotBothTakeTheLastSlot()
    {
        // FIL-002. Two contexts see the same container with 49 filed; each files one more. The
        // first save wins; the second conflicts on the container's stamp and records nothing.
        var fileId = await ActiveFileAsync(1, 50);
        var container = await _harness.Db.PhysicalFileContainers.SingleAsync(c => c.Id == fileId);
        for (var i = 0; i < 49; i++) container.RecordFiled();
        await _harness.Db.SaveChangesAsync();
        _harness.Db.ChangeTracker.Clear();

        var a = await AcceptedVoucherAsync("049-26");
        var b = await AcceptedVoucherAsync("050-26");
        _harness.SignInAsCustodian();

        using var second = _harness.CreateSecondContext();
        var serviceA = Service();
        var serviceB = new PhysicalDocumentService(second, new Emc.Application.Authorization.EvidenceAuthorizationService(second, _harness.CurrentUser, _harness.Clock), _harness.CurrentUser,
            new Emc.Application.Audit.AuditRecorder(second, _harness.CurrentUser, _harness.Clock, new TestRequestContext()), _harness.Clock);

        // Both read the container at 49 before either writes: simulate by loading in B first.
        var containerInB = await second.PhysicalFileContainers.SingleAsync(c => c.Id == fileId);
        Assert.Equal(49, containerInB.FiledVoucherCount);

        var resultA = await serviceA.RecordAsync(new PhysicalDocumentActionRequest(a, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.True(resultA.Succeeded, resultA.Error);

        var resultB = await serviceB.RecordAsync(new PhysicalDocumentActionRequest(b, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.False(resultB.Succeeded);
        Assert.Equal("FIL-002", resultB.RequirementId);

        _harness.Db.ChangeTracker.Clear();
        var stored = await _harness.Db.PhysicalFileContainers.AsNoTracking().SingleAsync(c => c.Id == fileId);
        Assert.Equal(50, stored.FiledVoucherCount);
        Assert.Equal(1, await _harness.Db.PhysicalVoucherDocuments.AsNoTracking().CountAsync(d => d.CurrentContainerId == fileId));
        Assert.True(stored.FiledVoucherCount <= PhysicalFileContainer.ActiveFileVoucherCapacity);

        // A retry against the current state is refused on the count itself (the binder is full).
        var retry = await Service().RecordAsync(new PhysicalDocumentActionRequest(b, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.Equal("FIL-002", retry.RequirementId);
    }

    [Fact]
    public async Task AnUnnumberedVoucherHasNoOriginalToFile()
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Paper test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD", "SUBJECT residence", _harness.Clock.UtcNow, false, null));
        var fileId = await ActiveFileAsync();
        var result = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucher.Value, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, fileId));
        Assert.False(result.Succeeded);
        Assert.Equal("FIL-004", result.RequirementId);
    }

    [Fact]
    public async Task InactiveFilingRequiresEveryItemDisposed_AndTransferTakesTheTransferPath()
    {
        var now = _harness.Clock.UtcNow;
        var voucherId = await AcceptedVoucherAsync("003-26");
        var active = await ActiveFileAsync();
        var inactive = await FolderAsync(PhysicalFileKind.Inactive4137File, "INACTIVE", now.Year, now.Month);
        Assert.True((await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, now, active))).Succeeded);

        var tooEarly = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInactive, now, inactive));
        Assert.Equal("FIL-006", tooEarly.RequirementId);

        // Every item permanently transferred: generic inactive filing is refused; the transfer path works.
        await SetItemsAsync(voucherId, AccountabilityStatus.PermanentlyTransferred);
        _harness.SignInAsCustodian();
        var generic = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInactive, now, inactive));
        Assert.Equal("FIL-006", generic.RequirementId);
        Assert.Contains("2-7g", generic.Error, StringComparison.Ordinal);
        var view = await Service().GetForVoucherAsync(voucherId);
        Assert.Equal(OriginalDisposition.HeldActive, view!.OriginalDisposition);
        Assert.Equal(VoucherClosureBasis.AllItemsPermanentlyTransferred, view.ClosureBasis);

        var transfer = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.TransferOriginalToGainingRoom, now, inactive, "Transferred.", CopyRetentionReason.None, "TEST GAINING ROOM"));
        Assert.True(transfer.Succeeded, transfer.Error);
        view = await Service().GetForVoucherAsync(voucherId);
        Assert.Equal(OriginalDisposition.TransferredToGainingRoom, view!.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.InactiveCopy, view.RetainedPaperStatus);
        Assert.Equal(0, (await Service().GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == active).VouchersFiled);
    }

    [Fact]
    public async Task TheRoundTrip_ReleaseReturnInactiveEligibility_CountsWhatEachContainerHolds()
    {
        var now = _harness.Clock.UtcNow;
        var voucherId = await AcceptedVoucherAsync("004-26");
        var active = await ActiveFileAsync();
        var usacil = await FolderAsync(PhysicalFileKind.SuspenseUsacil, "USACIL");
        Assert.True((await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, now, active))).Succeeded);

        var released = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.ReleaseOriginalWithEvidence, now.AddDays(1), usacil, "To USACIL"));
        Assert.True(released.Succeeded, released.Error);
        var rows = await Service().GetContainersAsync(_harness.EvidenceRoomId);
        Assert.Equal(0, rows.Single(c => c.Id == active).VouchersFiled);   // the original is out
        Assert.Equal(1, rows.Single(c => c.Id == usacil).VouchersFiled);   // the first copy is here

        var returned = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.ReturnOriginalToActiveFile, now.AddDays(20), active, "Back from USACIL"));
        Assert.True(returned.Succeeded, returned.Error);
        rows = await Service().GetContainersAsync(_harness.EvidenceRoomId);
        Assert.Equal(1, rows.Single(c => c.Id == active).VouchersFiled);
        Assert.Equal(0, rows.Single(c => c.Id == usacil).VouchersFiled);
        var view = await Service().GetForVoucherAsync(voucherId);
        Assert.True(view!.SuspenseCopyFiledWithOriginal);

        await SetItemsAsync(voucherId, AccountabilityStatus.DispositionPending);
        await SetItemsAsync(voucherId, AccountabilityStatus.Disposed);
        _harness.SignInAsCustodian();
        var inactiveAt = now.AddDays(30);
        var inactive = await FolderAsync(PhysicalFileKind.Inactive4137File, "INACTIVE", inactiveAt.Year, inactiveAt.Month);
        var wrongMonth = await FolderAsync(PhysicalFileKind.Inactive4137File, "INACTIVE WRONG", inactiveAt.AddMonths(-2).Year, inactiveAt.AddMonths(-2).Month);
        var mismatch = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInactive, inactiveAt, wrongMonth));
        Assert.Equal("FIL-013", mismatch.RequirementId);
        var filedInactive = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInactive, inactiveAt, inactive));
        Assert.True(filedInactive.Succeeded, filedInactive.Error);

        view = await Service().GetForVoucherAsync(voucherId);
        Assert.Equal(OriginalDisposition.FiledInactive, view!.OriginalDisposition);
        Assert.Equal(PaperRetentionStatus.Retain, view.RetentionStatus);
        Assert.Equal(inactiveAt.AddYears(3), view.DestructionEligibleAtUtc);
        Assert.Equal(6, view.Events.Count);
    }

    [Fact]
    public async Task AContainerFromAnotherRoomIsNotFound()
    {
        var voucherId = await AcceptedVoucherAsync("005-26");
        var foreign = new PhysicalFileContainer(_harness.OtherEvidenceRoomId, PhysicalFileKind.Active4137File, ContainerForm.Binder, "OTHER ROOM", 2026, 1, 50, "001-26", "050-26");
        _harness.Db.PhysicalFileContainers.Add(foreign);
        await _harness.Db.SaveChangesAsync();
        _harness.SignInAsCustodian();
        var result = await Service().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, foreign.Id));
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
