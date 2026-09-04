using System.Net;
using Emc.Application.Cases;
using Emc.Application.Filing;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Physical/digital cross-checks (advisories only) and the paper retention dashboard.
/// Requirements: PDC-001, FIL-010, RET-007, SUSP-007.
/// </summary>
public class PaperRecordReportTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private IPhysicalDocumentService Physical() => new PhysicalDocumentService(_harness.Db, _harness.Authorization, _harness.CurrentUser, _harness.Audit, _harness.Clock);
    private IPhysicalDigitalConsistencyService Consistency() => new PhysicalDigitalConsistencyService(_harness.Db, _harness.Authorization, _harness.Clock);
    private IRetentionDashboardService Dashboard() => new RetentionDashboardService(_harness.Db, _harness.Authorization, Physical(), _harness.Clock);

    private async Task<int> AcceptedVoucherAsync(string number)
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Paper report test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A., SGT", _harness.Clock.UtcNow, false, null));
        await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "One test item", "1", null, null, false, false, false, null));
        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucher.Value);
        _harness.SignInAsCustodian();
        var numbered = await _harness.Intake.RecordOfficialDocumentNumberAsync(new RecordDocumentNumberRequest(voucher.Value, number, true, _harness.Clock.UtcNow));
        Assert.True(numbered.Succeeded, numbered.Error);
        return voucher.Value;
    }

    private async Task<int> ContainerAsync(PhysicalFileKind kind, string label, int? year = null, int? month = null)
    {
        _harness.SignInAsCustodian();
        var result = await Physical().CreateContainerAsync(kind == PhysicalFileKind.Active4137File
            ? new CreateFileContainerRequest(_harness.EvidenceRoomId, kind, ContainerForm.Folder, label, 2026, 1, 50)
            : new CreateFileContainerRequest(_harness.EvidenceRoomId, kind, ContainerForm.Folder, label, null, null, null, year, month));
        Assert.True(result.Succeeded, result.Error);
        return result.Value;
    }

    private async Task SetItemStatusAsync(int voucherId, AccountabilityStatus to, string reason)
    {
        var item = await _harness.Db.EvidenceItems.Include(i => i.Events).FirstAsync(i => i.VoucherId == voucherId);
        var now = _harness.Clock.UtcNow;
        var from = item.AccountabilityStatus;
        item.TransitionTo(to);
        await _harness.EventRecorder.AppendAsync(item, new StatusEvent(from, to, reason, now, now, _harness.CustodianUserId));
        await _harness.Db.SaveChangesAsync();
        _harness.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task AdvisoriesSayWhatDisagrees_AndChangeNothing()
    {
        var voucherId = await AcceptedVoucherAsync("010-26");

        // Accepted, nothing filed: PDC-001 and the no-companion-copy note.
        var advisories = await Consistency().GetAdvisoriesAsync(voucherId);
        Assert.Contains(advisories, a => a.Code == "PDC-001");
        Assert.Contains(advisories, a => a.Code == "PDC-020");

        var active = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26");
        Assert.True((await Physical().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, active))).Succeeded);
        advisories = await Consistency().GetAdvisoriesAsync(voucherId);
        Assert.DoesNotContain(advisories, a => a.Code == "PDC-001");

        // The item goes out on temporary release while the paper record still says "filed active" (2-7a, 2-4f(2)).
        await SetItemStatusAsync(voucherId, AccountabilityStatus.TemporarilyReleased, "Test: released to laboratory");
        advisories = await Consistency().GetAdvisoriesAsync(voucherId);
        Assert.Contains(advisories, a => a.Code == "PDC-002");

        // The paper is recorded as accompanying the release: consistent again.
        var suspense = await ContainerAsync(PhysicalFileKind.SuspenseUsacil, "SUSPENSE USACIL");
        Assert.True((await Physical().RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.ReleaseOriginalWithEvidence, _harness.Clock.UtcNow, suspense))).Succeeded);
        advisories = await Consistency().GetAdvisoriesAsync(voucherId);
        Assert.DoesNotContain(advisories, a => a.Code == "PDC-002");
        Assert.DoesNotContain(advisories, a => a.Code == "PDC-003");

        // The item comes back but the paper is still recorded as out: PDC-003.
        await SetItemStatusAsync(voucherId, AccountabilityStatus.InEvidenceRoom, "Test: returned from laboratory");
        advisories = await Consistency().GetAdvisoriesAsync(voucherId);
        Assert.Contains(advisories, a => a.Code == "PDC-003");

        // Nothing above changed a state: the paper record still says what was recorded.
        var paper = await Physical().GetForVoucherAsync(voucherId);
        Assert.Equal(OriginalDisposition.AccompanyingTemporaryRelease, paper!.OriginalDisposition);

        // An agent of another room sees no advisories at all.
        _harness.SignInAsAdministrator();
        Assert.Empty(await Consistency().GetAdvisoriesAsync(voucherId));
    }

    [Fact]
    public async Task TheDashboardBucketsByFileAndByTheThreeYearClock_FromTheInactiveDateOnly()
    {
        var active = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26");
        var inactive = await ContainerAsync(PhysicalFileKind.Inactive4137File, "INACTIVE " + _harness.Clock.UtcNow.ToString("MMM yyyy").ToUpperInvariant(), _harness.Clock.UtcNow.Year, _harness.Clock.UtcNow.Month);

        var filed = await AcceptedVoucherAsync("011-26");
        Assert.True((await Physical().RecordAsync(new PhysicalDocumentActionRequest(filed, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, active))).Succeeded);

        var unfiled = await AcceptedVoucherAsync("012-26");

        // An inactive voucher: every item disposed of, then the original filed inactive.
        var disposed = await AcceptedVoucherAsync("013-26");
        Assert.True((await Physical().RecordAsync(new PhysicalDocumentActionRequest(disposed, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, active))).Succeeded);
        await SetItemStatusAsync(disposed, AccountabilityStatus.DispositionPending, "Test: disposition approved");
        await SetItemStatusAsync(disposed, AccountabilityStatus.Disposed, "Test: disposed");
        var inactiveAt = _harness.Clock.UtcNow;
        Assert.True((await Physical().RecordAsync(new PhysicalDocumentActionRequest(disposed, PhysicalDocumentAction.FileOriginalInactive, inactiveAt, inactive))).Succeeded);

        var view = await Dashboard().GetAsync(_harness.EvidenceRoomId);
        Assert.NotNull(view);
        Assert.Contains(view.Active, r => r.VoucherId == filed);
        Assert.Contains(view.Unfiled, r => r.VoucherId == unfiled);
        Assert.Contains(view.InactiveRetain, r => r.VoucherId == disposed);
        Assert.Empty(view.InactiveEligibleForDestruction);
        Assert.Contains(view.Containers, c => c.Container.Id == active && c.Capacity == PhysicalFileContainer.ActiveFileVoucherCapacity && !c.OverCapacity);

        // Three years from the INACTIVE date - not from disposal, not from the case - and it is eligible.
        _harness.Clock.Advance(TimeSpan.FromDays(365 * 3 + 2));
        view = await Dashboard().GetAsync(_harness.EvidenceRoomId);
        Assert.Contains(view!.InactiveEligibleForDestruction, r => r.VoucherId == disposed);
        Assert.Contains(await Consistency().GetAdvisoriesAsync(disposed), a => a.Code == "PDC-008");

        // Confirmed by a person; the row moves, the digital record stays (DEC-07).
        Assert.True((await Physical().RecordAsync(new PhysicalDocumentActionRequest(disposed, PhysicalDocumentAction.ConfirmDestruction, _harness.Clock.UtcNow, null, "Destroyed per 2-4h; witnessed."))).Succeeded);
        view = await Dashboard().GetAsync(_harness.EvidenceRoomId);
        Assert.Contains(view!.InactiveDestructionConfirmed, r => r.VoucherId == disposed);
        Assert.NotNull(await _harness.Reads.GetVoucherAsync(disposed));

        // The dashboard is a read of the room; an outsider gets nothing.
        _harness.SignInAsAdministrator();
        Assert.Null(await Dashboard().GetAsync(_harness.EvidenceRoomId));
    }
}

public class PaperDashboardHttpTests : IClassFixture<EmcWebFactory>, IClassFixture<UnregisteredPrincipalWebFactory>
{
    private readonly EmcWebFactory _registered;
    private readonly UnregisteredPrincipalWebFactory _unregistered;

    public PaperDashboardHttpTests(EmcWebFactory registered, UnregisteredPrincipalWebFactory unregistered)
    {
        _registered = registered;
        _unregistered = unregistered;
    }

    [Fact]
    public async Task TheRetentionDashboardRendersForTheRoom_AndNotForAnOutsider()
    {
        var client = _registered.CreateClient();
        using var page = await client.GetAsync($"/Filing/Retention/{_registered.EvidenceRoomId}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Active file", html, StringComparison.Ordinal);
        Assert.Contains("eligible for destruction", html, StringComparison.Ordinal);
        Assert.Contains("DEC-07", html, StringComparison.Ordinal);

        using var denied = await _unregistered.CreateClient().GetAsync($"/Filing/Retention/{_registered.EvidenceRoomId}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }
}
