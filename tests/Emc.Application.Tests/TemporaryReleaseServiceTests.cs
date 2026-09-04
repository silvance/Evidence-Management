using Emc.Application.Cases;
using Emc.Application.Filing;
using Emc.Application.Suspense;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Filing;
using Emc.Domain.Suspense;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Temporary release as one unit of work: custody and status events on each item, the release
/// tied to those events, the paper original leaving and the first copy filed, all or nothing.
/// Requirements: SUSP-001, SUSP-003, SUSP-005, SUSP-007, SUSP-010, SUSP-011, COC-003, COC-006.
/// </summary>
public class TemporaryReleaseServiceTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<(int VoucherId, int FirstItem, int SecondItem)> AcceptedVoucherAsync(string number = "004-26")
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest($"CASE-{Guid.NewGuid():N}"[..20], "Release test", null, _harness.EvidenceRoomId));
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

    private static ReleaseRecipient Counsel(bool identification = true)
        => new(CustodyPartyKind.ExternalPerson, "COUNSEL, TEST B.", "CPT", "OSJA, Fort Test", identification);

    private TemporaryReleaseRequest Request(int voucherId, int folderId, SuspenseCategory category = SuspenseCategory.Adjudication, ReleaseRecipient? to = null, params int[] itemIds)
        => new(voucherId, itemIds, category, to ?? Counsel(), category == SuspenseCategory.Usacil ? "Forensic examination, USACIL (TEST)" : "Presentation at trial, US v. TEST", "Fort Test courtroom 2",
            _harness.Clock.UtcNow.AddHours(-3), folderId, true, true, true, true, true, _harness.Clock.UtcNow.AddDays(30), null, "TEST release note");

    [Fact]
    public async Task AReleaseWritesCustodyStatusPaperAndSuspenseTogether()
    {
        var (voucherId, item1, item2) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);

        var releasedAt = _harness.Clock.UtcNow.AddHours(-3);
        var result = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1, item2]) with { ReleasedAtLocal = releasedAt });
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("reasonable and adequate contact", StringComparison.Ordinal) && w.Contains("local management threshold", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        var view = (await _harness.Releases.GetAsync(result.Value))!;
        Assert.Equal(SuspenseCategory.Adjudication, view.Category);
        Assert.Equal(TemporaryReleaseStatus.Open, view.Status);
        Assert.Equal(2, view.Items.Count);
        Assert.Equal(2, view.ItemsOut);
        Assert.Equal("COUNSEL, TEST B.", view.ReceivedByDisplayName);
        Assert.Equal(CustodyPartyKind.ExternalPerson, view.ReceivedByKind);
        Assert.Equal("ADJUDICATION", view.SuspenseFolderLabel);
        Assert.True(view.PhysicalInventoryPerformedAttested && view.Original4137ReceivedBySignedAttested && view.FirstCopyReceivedBySignedAttested && view.IdentificationPresentedAttested && view.ObligationsInformedAttested);
        Assert.Equal(TemporaryReleaseEventKind.Released, Assert.Single(view.Events).Kind);

        // COC-003 / COC-005: a custody event per item, when it left vs when it was recorded, SCRCNI on the sealed item only.
        foreach (var (itemId, sealedItem) in new[] { (item1, false), (item2, true) })
        {
            var events = await _harness.Db.ItemEvents.AsNoTracking().Where(e => e.EvidenceItemId == itemId).OrderBy(e => e.SequenceNumber).ToListAsync();
            var custody = Assert.Single(events.OfType<CustodyEvent>());
            Assert.Equal(releasedAt, custody.OccurredAtUtc);
            Assert.Equal(_harness.Clock.UtcNow, custody.RecordedAtUtc);
            Assert.NotEqual(custody.OccurredAtUtc, custody.RecordedAtUtc);
            Assert.Equal(sealedItem, custody.IsScrcni);
            Assert.Equal("OSJA, Fort Test", custody.Agency);
            var status = events.OfType<StatusEvent>().Last();
            Assert.Equal(AccountabilityStatus.TemporarilyReleased, status.ToStatus);
            Assert.True(status.SequenceNumber > custody.SequenceNumber);

            var member = await _harness.Db.TemporaryReleaseItems.AsNoTracking().SingleAsync(t => t.EvidenceItemId == itemId);
            Assert.Equal(custody.Id, member.ReleaseCustodyEventId);
            Assert.Equal(TemporaryReleaseItemStatus.Out, member.Status);

            var item = await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
            Assert.Equal(AccountabilityStatus.TemporarilyReleased, item.AccountabilityStatus);
        }

        // The current custody holder, derived from the chain, is the recipient.
        var history = (await _harness.History.GetAsync(item1))!;
        Assert.Equal("COUNSEL, TEST B.", history.CurrentCustodyHolder);
        Assert.True(history.ChainVerification.IsIntact);

        // SUSP-007: the original left with the evidence; the first copy is in the ADJUDICATION folder.
        var paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(OriginalDisposition.AccompanyingTemporaryRelease, paper.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.SuspenseCopy, paper.RetainedPaperStatus);
        Assert.Equal("ADJUDICATION", paper.CurrentContainerLabel);
        var containers = await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId);
        Assert.Equal(0, containers.Single(c => c.Id == binder).VouchersFiled);
        Assert.Equal(1, containers.Single(c => c.Id == adjudication).VouchersFiled);

        // Listed on the voucher.
        var forVoucher = await _harness.Releases.GetForVoucherAsync(voucherId);
        Assert.Equal(result.Value, Assert.Single(forVoucher).Id);
    }

    [Fact]
    public async Task NothingIsWrittenWhenAnyPartFails()
    {
        // SUSP-010: a wrong folder, an unfiled original, an item not in the room - each refuses
        // BEFORE anything is written; the items, events and paper are untouched.
        var (voucherId, item1, item2) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var usacil = await ContainerAsync(PhysicalFileKind.SuspenseUsacil, "USACIL");
        var pending = await ContainerAsync(PhysicalFileKind.SuspensePendingDispositionApproval, "PENDING DISPOSITION APPROVAL");
        var eventsBefore = await _harness.Db.ItemEvents.CountAsync(e => e.EvidenceItemId == item1 || e.EvidenceItemId == item2);

        // Paper not filed yet.
        var unfiled = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, itemIds: [item1]));
        Assert.False(unfiled.Succeeded);
        Assert.Equal("FIL-005", unfiled.RequirementId);

        await FileOriginalAsync(voucherId, binder);

        // Wrong folder kind for the category.
        var wrongFolder = await _harness.Releases.ReleaseAsync(Request(voucherId, pending, SuspenseCategory.Adjudication, itemIds: [item1]));
        Assert.False(wrongFolder.Succeeded);
        Assert.Equal("FIL-005", wrongFolder.RequirementId);

        // The PENDING DISPOSITION APPROVAL category is not a release of evidence.
        var notARelease = await _harness.Releases.ReleaseAsync(Request(voucherId, pending, SuspenseCategory.PendingDispositionApproval, itemIds: [item1]));
        Assert.False(notARelease.Succeeded);
        Assert.Equal("SUSP-002", notARelease.RequirementId);

        // An item from another voucher.
        var (_, foreignItem, _) = await AcceptedVoucherAsync("005-26");
        var foreign = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, itemIds: [item1, foreignItem]));
        Assert.False(foreign.Succeeded);
        Assert.Equal("SUSP-001", foreign.RequirementId);

        _harness.Db.ChangeTracker.Clear();
        Assert.Equal(eventsBefore, await _harness.Db.ItemEvents.CountAsync(e => e.EvidenceItemId == item1 || e.EvidenceItemId == item2));
        Assert.Empty(_harness.Db.TemporaryReleases);
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item2)).AccountabilityStatus);
        Assert.Equal(OriginalDisposition.HeldActive, (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!.OriginalDisposition);
        Assert.Equal(0, (await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == usacil).VouchersFiled);
    }

    [Fact]
    public async Task TheAttestationsAreRequiredForAPerson_NotForAccountableMailToTheLaboratory()
    {
        var (voucherId, item1, item2) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var usacil = await ContainerAsync(PhysicalFileKind.SuspenseUsacil, "USACIL");
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);

        var missing = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]) with { Original4137ReceivedBySignedAttested = false });
        Assert.False(missing.Succeeded);
        Assert.Equal("SUSP-011", missing.RequirementId);
        Assert.Contains("ORIGINAL", missing.Error, StringComparison.Ordinal);

        var noId = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, to: Counsel(identification: false), itemIds: [item1]));
        Assert.False(noId.Succeeded);
        Assert.Equal("SUSP-011", noId.RequirementId);

        // 2-7e: mail to the USACIL; the number is the Received By entry; no counter attestations.
        var mail = new ReleaseRecipient(CustodyPartyKind.AccountableMailNumber, "RA 000 000 000 US", AccountableMailNumber: "RA 000 000 000 US", Carrier: "USPS registered");
        var mailed = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, mail, item1, item2) with
        {
            PhysicalInventoryPerformedAttested = false, Original4137ReceivedBySignedAttested = false, FirstCopyReceivedBySignedAttested = false,
            IdentificationPresentedAttested = false, ObligationsInformedAttested = false
        });
        Assert.True(mailed.Succeeded, mailed.Error);
        Assert.Contains(mailed.Warnings, w => w.Contains("2-7e", StringComparison.Ordinal));
        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().Include(c => c.ReceivedBy).AsNoTracking().Where(c => c.EvidenceItemId == item1).SingleAsync();
        Assert.Equal(CustodyPartyKind.AccountableMailNumber, custody.ReceivedBy.Kind);
        Assert.Equal("RA 000 000 000 US", custody.ReceivedBy.AccountableMailNumber);
    }

    [Fact]
    public async Task AnItemOutIsNotReleasedAgain_AndTheOriginalOutBlocksASecondReleaseUntilCopiesAreRecorded()
    {
        var (voucherId, item1, item2) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);

        var first = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.True(first.Succeeded, first.Error);

        var again = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.False(again.Succeeded);
        Assert.Equal("SUSP-001", again.RequirementId);
        Assert.Contains("already on temporary release", again.Error, StringComparison.Ordinal);

        // Item 2 is in the room, but the ORIGINAL is out with item 1: the copy path (SUSP-008) is needed.
        var second = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item2]));
        Assert.False(second.Succeeded);
        Assert.Equal("FIL-005", second.RequirementId);
        Assert.Contains("SUSP-008", second.Error, StringComparison.Ordinal);

        // The database refuses a second OUT row for one item regardless of the application.
        _harness.Db.ChangeTracker.Clear();
        var release = await _harness.Db.TemporaryReleases.Include(r => r.Items).Include(r => r.ReleasedBy).Include(r => r.ReceivedBy).SingleAsync();
        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().SingleAsync(c => c.EvidenceItemId == item1);
        var other = TemporaryRelease.Create(voucherId, _harness.EvidenceRoomId, SuspenseCategory.Adjudication, release.ReleasedBy, release.ReceivedBy, "x", null, _harness.Clock.UtcNow, _harness.Clock.UtcNow, _harness.CustodianUserId, null, new(true, true, true, true, true), adjudication);
        other.AddItem(item1, 1, custody);
        other.MarkReleased(_harness.CustodianUserId, _harness.Clock.UtcNow, null);
        _harness.Db.TemporaryReleases.Add(other);
        await Assert.ThrowsAsync<DbUpdateException>(() => _harness.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task ContactsAreAppendOnly_AndDaysOutIsACount()
    {
        var (voucherId, item1, _) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);
        var release = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.True(release.Succeeded, release.Error);

        _harness.Clock.Advance(TimeSpan.FromDays(45));
        var contact = await _harness.Releases.RecordContactAsync(new RecordSuspenseContactRequest(release.Value, _harness.Clock.UtcNow.AddHours(-1), ContactMethod.Telephone, "COUNSEL, TEST B.", ContactOutcome.EvidenceStillRequired, "Trial continued to next term (TEST).", _harness.Clock.UtcNow.AddDays(30)));
        Assert.True(contact.Succeeded, contact.Error);

        _harness.Db.ChangeTracker.Clear();
        var view = (await _harness.Releases.GetAsync(release.Value))!;
        Assert.Equal(45, view.DaysOut);
        Assert.Single(view.Contacts);
        Assert.Equal(ContactMethod.Telephone, view.Contacts[0].Method);
        Assert.NotNull(view.LastContactAtUtc);
        Assert.Equal(_harness.Clock.UtcNow.AddDays(30), view.ExpectedFollowUpLocal);
        Assert.DoesNotContain("deadline", string.Join(' ', typeof(TemporaryReleaseView).GetProperties().Select(p => p.Name)), StringComparison.OrdinalIgnoreCase);

        var stored = await _harness.Db.SuspenseContacts.SingleAsync();
        _harness.Db.Entry(stored).Property(nameof(SuspenseContact.Narrative)).CurrentValue = "edited";
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.ChangeTracker.Clear();
        var releaseEvent = await _harness.Db.Set<TemporaryReleaseEvent>().FirstAsync();
        _harness.Db.Remove(releaseEvent);
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => _harness.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task OnlyAnAppointedCustodianReleases_AndReadsAreRoomScoped()
    {
        var (voucherId, item1, _) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);

        _harness.SignInAsAgent();
        var agent = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.False(agent.Succeeded);
        Assert.Contains(_harness.Db.AuditEvents, a => a.EventType == AuditEventType.PermissionDenied && a.AffectedRecordType == nameof(TemporaryRelease));

        _harness.SignInAsAdministrator();
        Assert.False((await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]))).Succeeded);

        _harness.SignInAsCustodian();
        var release = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.True(release.Succeeded, release.Error);

        _harness.CurrentUser.SignIn(_harness.SecondAgentUserId, "SA PATEL, ANIKA R.", _harness.OtherEvidenceRoomId, Emc.Domain.Identity.EmcRoles.Agent);
        Assert.Null(await _harness.Releases.GetAsync(release.Value));
        Assert.Empty(await _harness.Releases.GetForVoucherAsync(voucherId));
        Assert.False((await _harness.Releases.RecordContactAsync(new RecordSuspenseContactRequest(release.Value, _harness.Clock.UtcNow, ContactMethod.Email, "x", ContactOutcome.Other, null))).Succeeded);
    }
}
