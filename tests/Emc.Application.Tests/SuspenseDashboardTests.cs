using Emc.Application.Cases;
using Emc.Application.Filing;
using Emc.Application.Suspense;
using Emc.Domain.Common;
using Emc.Domain.Filing;
using Emc.Domain.Suspense;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The suspense dashboard (SUSP-004, SUSP-006, SUSP-017): the regulation's three folders, days
/// out as a count, the one threshold labelled LOCAL, follow-ups the custodian set for
/// themselves, and advisories where the release records disagree with the rest of the
/// companion record. Read-only: nothing here changes a state.
/// </summary>
public class SuspenseDashboardTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<(int VoucherId, int FirstItem, int SecondItem)> AcceptedVoucherAsync(string number)
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Dashboard test", null, _harness.EvidenceRoomId));
        var voucher = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(caseResult.Value, "TEST EVIDENCE ROOM", "FORT TEST, TS", "SMITH, TEST A.", _harness.Clock.UtcNow, false, null));
        var i1 = await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "ONE TEST MOBILE TELEPHONE, BLACK", "1", "TESTSERIAL000001", null, false, false, false, null));
        var i2 = await _harness.Vouchers.AddItemAsync(new AddItemRequest(voucher.Value, "ONE TEST SEALED ENVELOPE", "1", null, null, false, false, true, "sealed in a paper envelope marked for identification (TEST)"));
        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucher.Value);
        _harness.SignInAsCustodian();
        var numbered = await _harness.Intake.RecordOfficialDocumentNumberAsync(new RecordDocumentNumberRequest(voucher.Value, number, true, _harness.Clock.UtcNow));
        Assert.True(numbered.Succeeded, numbered.Error);
        return (voucher.Value, i1.Value, i2.Value);
    }

    private async Task<int> ContainerAsync(PhysicalFileKind kind, string label, int? from = null, int? to = null)
    {
        _harness.SignInAsCustodian();
        var result = await _harness.PhysicalDocuments.CreateContainerAsync(kind == PhysicalFileKind.Active4137File
            ? new CreateFileContainerRequest(_harness.EvidenceRoomId, kind, ContainerForm.Binder, label, 2026, from, to)
            : new CreateFileContainerRequest(_harness.EvidenceRoomId, kind, ContainerForm.Folder, label));
        Assert.True(result.Succeeded, result.Error);
        return result.Value;
    }

    private async Task FileOriginalAsync(int voucherId, int binderId)
    {
        _harness.SignInAsCustodian();
        var filed = await _harness.PhysicalDocuments.RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.FileOriginalInActiveFile, _harness.Clock.UtcNow, binderId));
        Assert.True(filed.Succeeded, filed.Error);
    }

    private sealed record Room(int Binder, int Usacil, int Adjudication, int Pending);

    private async Task<Room> FoldersAsync()
    {
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var usacil = await ContainerAsync(PhysicalFileKind.SuspenseUsacil, "USACIL");
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        var pending = await ContainerAsync(PhysicalFileKind.SuspensePendingDispositionApproval, "PENDING DISPOSITION APPROVAL");
        return new Room(binder, usacil, adjudication, pending);
    }

    private TemporaryReleaseRequest Adjudication(int voucherId, int folderId, params int[] itemIds)
        => new(voucherId, itemIds, SuspenseCategory.Adjudication, new ReleaseRecipient(CustodyPartyKind.ExternalPerson, "COUNSEL, TEST B.", "CPT", "OSJA, Fort Test", true),
            "Presentation at trial, US v. TEST", "Fort Test courtroom 2", _harness.Clock.UtcNow.AddHours(-3), folderId, true, true, true, true, true,
            _harness.Clock.UtcNow.AddDays(30), null, "TEST release note");

    private TemporaryReleaseRequest UsacilByMail(int voucherId, int folderId, params int[] itemIds)
        => new(voucherId, itemIds, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.AccountableMailNumber, "USACIL", null, "USACIL", false, "TEST-MAIL-0000000001", "USPS registered (TEST)"),
            "Forensic examination, USACIL (TEST)", "USACIL, Forest Park, GA", _harness.Clock.UtcNow.AddHours(-3), folderId, false, false, false, false, false,
            null, null, null, PaperCopyKind.Original, new LaboratoryDetails("USACIL", ExaminationRequestReference: "DD 2922 TEST-0001"));

    [Fact]
    public async Task RowsSitUnderTheRegulationsFolders_DaysOutIsACount_AndTheOnlyThresholdIsLocal()
    {
        var room = await FoldersAsync();
        var (adjudicationVoucher, adjudicationItem, _) = await AcceptedVoucherAsync("004-26");
        await FileOriginalAsync(adjudicationVoucher, room.Binder);
        var (usacilVoucher, usacilItem, _) = await AcceptedVoucherAsync("005-26");
        await FileOriginalAsync(usacilVoucher, room.Binder);

        var toCounsel = await _harness.Releases.ReleaseAsync(Adjudication(adjudicationVoucher, room.Adjudication, adjudicationItem));
        Assert.True(toCounsel.Succeeded, toCounsel.Error);
        var toLab = await _harness.Releases.ReleaseAsync(UsacilByMail(usacilVoucher, room.Usacil, usacilItem));
        Assert.True(toLab.Succeeded, toLab.Error);

        _harness.Db.ChangeTracker.Clear();
        var fresh = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        Assert.Equal(60, fresh.LocalReviewThresholdDays);
        Assert.Equal(2, fresh.OpenReleases);
        Assert.Empty(fresh.PendingDispositionApproval);
        Assert.Empty(fresh.RecentlyClosed);
        Assert.Empty(fresh.Advisories);

        var counselRow = Assert.Single(fresh.Adjudication);
        Assert.Equal(toCounsel.Value, counselRow.ReleaseId);
        Assert.Equal("004-26", counselRow.VoucherIdentifier);
        Assert.Equal("COUNSEL, TEST B.", counselRow.HeldBy);
        Assert.Equal(CustodyPartyKind.ExternalPerson, counselRow.HeldByKind);
        Assert.Equal("ADJUDICATION", counselRow.SuspenseFolderLabel);
        Assert.Equal(0, counselRow.DaysOut);
        Assert.Equal("1", counselRow.ItemNumbers);
        Assert.False(counselRow.FollowUpDue);
        Assert.False(counselRow.ExceedsLocalReviewThreshold);

        var labRow = Assert.Single(fresh.Usacil);
        Assert.Equal("USACIL", labRow.LaboratoryName);
        Assert.Equal(CustodyPartyKind.AccountableMailNumber, labRow.HeldByKind);
        Assert.Equal(PaperCopyKind.Original, labRow.PaperAccompanying);
        Assert.Null(labRow.LastContactAtUtc);

        // Days out is a count from the release, whatever the threshold; the threshold is the
        // room's LOCAL number and nothing in the view calls it a deadline.
        _harness.Clock.Advance(TimeSpan.FromDays(45));
        _harness.Db.ChangeTracker.Clear();
        var later = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        Assert.Equal(45, later.Adjudication[0].DaysOut);
        Assert.False(later.Adjudication[0].ExceedsLocalReviewThreshold);
        Assert.True(later.Adjudication[0].FollowUpDue, "the custodian's own 30-day follow-up has passed");
        Assert.False(later.Usacil[0].FollowUpDue, "no follow-up was set for the laboratory release");
        Assert.Equal(1, later.FollowUpsDue);

        var configuration = await _harness.Db.SystemConfigurations.SingleAsync();
        configuration.SetLocalSuspenseReviewThreshold(30);
        await _harness.Db.SaveChangesAsync();
        _harness.Db.ChangeTracker.Clear();
        var lowered = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        Assert.Equal(30, lowered.LocalReviewThresholdDays);
        Assert.True(lowered.Adjudication[0].ExceedsLocalReviewThreshold);
        Assert.True(lowered.Usacil[0].ExceedsLocalReviewThreshold);
        Assert.Equal(2, lowered.ExceedingLocalThreshold);

        var names = string.Join(' ', typeof(SuspenseRow).GetProperties().Concat(typeof(SuspenseDashboardView).GetProperties()).Select(p => p.Name));
        Assert.DoesNotContain("deadline", names, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overdue", names, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LocalReviewThreshold", names, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContactResetsTheLastContactColumn_AndAClosedReleaseMovesToRecentlyClosed()
    {
        var room = await FoldersAsync();
        var (voucherId, item, _) = await AcceptedVoucherAsync("006-26");
        await FileOriginalAsync(voucherId, room.Binder);
        await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(item, _harness.ShelfBBin14Id, _harness.Clock.UtcNow, "Initial placement (TEST)", null));
        var release = await _harness.Releases.ReleaseAsync(Adjudication(voucherId, room.Adjudication, item));
        Assert.True(release.Succeeded, release.Error);

        _harness.Clock.Advance(TimeSpan.FromDays(10));
        var contact = await _harness.Releases.RecordContactAsync(new RecordSuspenseContactRequest(release.Value, _harness.Clock.UtcNow.AddHours(-1), ContactMethod.Telephone, "COUNSEL, TEST B.", ContactOutcome.EvidenceStillRequired, "Trial continued (TEST).", _harness.Clock.UtcNow.AddDays(14)));
        Assert.True(contact.Succeeded, contact.Error);

        _harness.Db.ChangeTracker.Clear();
        var chased = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        var row = Assert.Single(chased.Adjudication);
        Assert.Equal(10, row.DaysOut);
        Assert.Equal(0, row.DaysSinceLastContact);
        Assert.Equal(_harness.Clock.UtcNow.AddDays(14), row.ExpectedFollowUpLocal);
        Assert.False(row.FollowUpDue);

        _harness.Clock.Advance(TimeSpan.FromDays(5));
        var back = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value,
            [new ReturnedItem(item, ConfirmReturnToPriorLocation: true)], _harness.Clock.UtcNow.AddHours(-1), true, true));
        Assert.True(back.Succeeded, back.Error);

        _harness.Db.ChangeTracker.Clear();
        var closed = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        Assert.Empty(closed.Adjudication);
        Assert.Equal(0, closed.OpenReleases);
        var closedRow = Assert.Single(closed.RecentlyClosed);
        Assert.Equal(release.Value, closedRow.ReleaseId);
        Assert.Equal(15, closedRow.DaysOut);
        Assert.Equal(0, closedRow.ItemsOut);
        Assert.Empty(closed.Advisories);
    }

    [Fact]
    public async Task PendingDispositionApprovalRowsComeFromThePaperRecord_NotFromAReleaseOfEvidence()
    {
        var room = await FoldersAsync();
        var (voucherId, item, _) = await AcceptedVoucherAsync("007-26");
        await FileOriginalAsync(voucherId, room.Binder);

        // 2-4f(3)(c): the ORIGINAL goes to trial counsel for disposition approval; the evidence stays.
        var sent = await _harness.PhysicalDocuments.RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.SendOriginalForDispositionApproval, _harness.Clock.UtcNow, room.Pending));
        Assert.True(sent.Succeeded, sent.Error);
        _harness.Clock.Advance(TimeSpan.FromDays(3));

        _harness.Db.ChangeTracker.Clear();
        var view = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        Assert.Equal(0, view.OpenReleases);
        var row = Assert.Single(view.PendingDispositionApproval);
        Assert.Null(row.ReleaseId);
        Assert.Equal(SuspenseCategory.PendingDispositionApproval, row.Category);
        Assert.Equal("007-26", row.VoucherIdentifier);
        Assert.Equal(PaperCopyKind.Original, row.PaperAccompanying);
        Assert.Equal(3, row.DaysOut);
        Assert.Equal(0, row.ItemsOut);
        Assert.Equal("PENDING DISPOSITION APPROVAL", row.SuspenseFolderLabel);
        Assert.Contains("disposition approval", row.HeldBy, StringComparison.Ordinal);

        var stored = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item);
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, stored.AccountabilityStatus);
        Assert.Empty(await _harness.Db.TemporaryReleases.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AdvisoriesNameEachDisagreement_ChangeNothing_AndReachTheVoucherConsistencyReport()
    {
        var room = await FoldersAsync();
        var (voucherId, released, stayed) = await AcceptedVoucherAsync("008-26");
        await FileOriginalAsync(voucherId, room.Binder);
        var release = await _harness.Releases.ReleaseAsync(Adjudication(voucherId, room.Adjudication, released));
        Assert.True(release.Succeeded, release.Error);

        _harness.Db.ChangeTracker.Clear();
        Assert.Empty((await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!.Advisories);

        // Disagreements a person must look at, written round the services on purpose: the
        // released item's state says it is back (SCV-002); the other item's state says it is
        // out with no release record (SCV-001); the paper record says the original is back in
        // the active file while the release says it left with the evidence (SCV-004).
        var items = await _harness.Db.EvidenceItems.Where(i => i.VoucherId == voucherId).ToListAsync();
        items.Single(i => i.Id == released).TransitionTo(AccountabilityStatus.InEvidenceRoom);
        items.Single(i => i.Id == stayed).TransitionTo(AccountabilityStatus.TemporarilyReleased);
        await _harness.Db.SaveChangesAsync();
        var paperBack = await _harness.PhysicalDocuments.RecordAsync(new PhysicalDocumentActionRequest(voucherId, PhysicalDocumentAction.ReturnOriginalToActiveFile, _harness.Clock.UtcNow, room.Binder));
        Assert.True(paperBack.Succeeded, paperBack.Error);

        _harness.Db.ChangeTracker.Clear();
        var view = (await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId))!;
        Assert.Contains(view.Advisories, a => a.Code == "SCV-001" && a.VoucherIdentifier == "008-26" && a.Message.Contains("Item 2", StringComparison.Ordinal));
        Assert.Contains(view.Advisories, a => a.Code == "SCV-002" && a.Message.Contains("Item 1", StringComparison.Ordinal));
        Assert.Contains(view.Advisories, a => a.Code == "SCV-004" && a.Regulation.Contains("2-7b", StringComparison.Ordinal));
        Assert.All(view.Advisories, a => Assert.DoesNotContain("deadline", a.Message, StringComparison.OrdinalIgnoreCase));

        // The release row is still shown as open: an advisory never changes a state.
        Assert.Single(view.Adjudication);
        var stillOpen = await _harness.Db.TemporaryReleases.AsNoTracking().SingleAsync();
        Assert.Equal(TemporaryReleaseStatus.Open, stillOpen.Status);

        // The same advisories surface on the voucher's own physical/digital consistency report.
        var consistency = new PhysicalDigitalConsistencyService(_harness.Db, _harness.Authorization, _harness.Clock);
        var forVoucher = await consistency.GetAdvisoriesAsync(voucherId);
        Assert.Contains(forVoucher, a => a.Code == "SCV-001");
        Assert.Contains(forVoucher, a => a.Code == "SCV-002");
        Assert.Contains(forVoucher, a => a.Code == "SCV-004");
    }

    [Fact]
    public async Task TheDashboardIsARoomScopedRead()
    {
        var room = await FoldersAsync();
        var (voucherId, item, _) = await AcceptedVoucherAsync("009-26");
        await FileOriginalAsync(voucherId, room.Binder);
        var release = await _harness.Releases.ReleaseAsync(Adjudication(voucherId, room.Adjudication, item));
        Assert.True(release.Succeeded, release.Error);
        _harness.Db.ChangeTracker.Clear();

        _harness.SignInAsAgent();
        Assert.NotNull(await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId));

        _harness.SignInAsCustodian();
        var otherRoom = (await _harness.SuspenseDashboard.GetAsync(_harness.OtherEvidenceRoomId));
        Assert.Null(otherRoom);
        Assert.Null(await _harness.SuspenseDashboard.GetAsync(999_999));

        _harness.SignInAsAdministrator();
        Assert.Null(await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId));

        _harness.CurrentUser.SignIn(_harness.SecondAgentUserId, "SA PATEL, ANIKA R.", _harness.OtherEvidenceRoomId, Emc.Domain.Identity.EmcRoles.Agent);
        Assert.Null(await _harness.SuspenseDashboard.GetAsync(_harness.EvidenceRoomId));
    }
}
