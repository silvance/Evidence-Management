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
            _harness.Clock.UtcNow.AddHours(-3), folderId, true, true, true, true, true, _harness.Clock.UtcNow.AddDays(30), null, "TEST release note",
            Laboratory: category == SuspenseCategory.Usacil ? new LaboratoryDetails("USACIL", ExaminationRequestReference: "DD 2922 TEST-0001") : null);

    private async Task<(int VoucherId, int FirstItem, int SecondItem, int Binder, int Usacil, int Adjudication)> ReadyAsync(string number = "004-26", string suffix = "")
    {
        var (voucherId, item1, item2) = await AcceptedVoucherAsync(number);
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, $"ACTIVE 001-26 to 050-26{suffix}", 1, 50);
        var usacil = await ContainerAsync(PhysicalFileKind.SuspenseUsacil, $"USACIL{suffix}");
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, $"ADJUDICATION{suffix}");
        await FileOriginalAsync(voucherId, binder);
        return (voucherId, item1, item2, binder, usacil, adjudication);
    }

    private async Task PlaceAsync(int itemId, int locationId)
    {
        _harness.SignInAsCustodian();
        var placed = await _harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(itemId, locationId, _harness.Clock.UtcNow, "Initial placement (TEST)", null));
        Assert.True(placed.Succeeded, placed.Error);
    }

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
    public async Task ASecondRecipientGetsACopyWhileTheOriginalIsOut()
    {
        // AR 195-5 2-7b, SUSP-008. Item 1 went to the laboratory with the original. Item 2 goes to
        // trial counsel with a COPY; the chain is recorded on the first copy in the USACIL folder.
        var (voucherId, item1, item2) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var usacil = await ContainerAsync(PhysicalFileKind.SuspenseUsacil, "USACIL");
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);

        var first = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "USACIL (TEST)"), item1));
        Assert.True(first.Succeeded, first.Error);

        // The copy path names the folder holding the first copy, not the category's folder.
        var wrongFolder = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item2]) with { PaperAccompanying = PaperCopyKind.AdditionalTemporaryReleaseCopy });
        Assert.False(wrongFolder.Succeeded);
        Assert.Equal("FIL-015", wrongFolder.RequirementId);
        Assert.Contains("USACIL", wrongFolder.Error, StringComparison.Ordinal);

        var second = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, itemIds: [item2]) with { PaperAccompanying = PaperCopyKind.AdditionalTemporaryReleaseCopy });
        Assert.True(second.Succeeded, second.Error);
        Assert.Contains(second.Warnings, w => w.Contains("COPY accompanied", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        var releases = await _harness.Releases.GetForVoucherAsync(voucherId);
        Assert.Equal(2, releases.Count);
        Assert.Contains(releases, r => r.PaperAccompanying == PaperCopyKind.Original && r.Category == SuspenseCategory.Usacil);
        Assert.Contains(releases, r => r.PaperAccompanying == PaperCopyKind.AdditionalTemporaryReleaseCopy && r.Category == SuspenseCategory.Adjudication && r.SuspenseFolderLabel == "USACIL");

        var paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(OriginalDisposition.AccompanyingTemporaryRelease, paper.OriginalDisposition);
        Assert.Equal(1, paper.AdditionalCopiesOut);
        Assert.True(paper.CopiesMadeNoted);
        Assert.Equal("USACIL", paper.FirstCopyContainerLabel);
        Assert.Contains(paper.Events, e => e.Kind == PhysicalDocumentEventKind.CopiesMadeNotedOnOriginalAndFirstCopy);
        Assert.Equal(1, (await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == usacil).VouchersFiled);

        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().SingleAsync(c => c.EvidenceItemId == item2);
        Assert.Contains("copy of DA Form 4137 accompanies", custody.Notes, StringComparison.Ordinal);
        Assert.Equal(AccountabilityStatus.TemporarilyReleased, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item2)).AccountabilityStatus);
    }

    [Fact]
    public async Task TwoRecipientsAtOnceUseCopies_TheOriginalStaysInTheBinder_AllOrNothing()
    {
        // AR 195-5 2-7b, SUSP-008: one request, two recipients, two releases, copies for both,
        // the original in its binder with the note, the first copy in the suspense folder.
        var (voucherId, item1, item2) = await AcceptedVoucherAsync();
        var binder = await ContainerAsync(PhysicalFileKind.Active4137File, "ACTIVE 001-26 to 050-26", 1, 50);
        var adjudication = await ContainerAsync(PhysicalFileKind.SuspenseAdjudication, "ADJUDICATION");
        await FileOriginalAsync(voucherId, binder);

        RecipientReleasePart Part(int itemId, string name) => new([itemId], SuspenseCategory.Adjudication, new ReleaseRecipient(CustodyPartyKind.ExternalPerson, name, "CPT", "OSJA, Fort Test", true),
            "Presentation at trial, US v. TEST", "Fort Test courtroom", true, true, true, true, true);

        // One recipient is not a multi-recipient release; an item twice is refused; a foreign item fails the whole request.
        Assert.Equal("SUSP-008", (await _harness.Releases.ReleaseToMultipleAsync(new MultiRecipientReleaseRequest(voucherId, _harness.Clock.UtcNow, adjudication, [Part(item1, "COUNSEL, TEST A.")]))).RequirementId);
        Assert.Equal("SUSP-008", (await _harness.Releases.ReleaseToMultipleAsync(new MultiRecipientReleaseRequest(voucherId, _harness.Clock.UtcNow, adjudication, [Part(item1, "COUNSEL, TEST A."), Part(item1, "COUNSEL, TEST B.")]))).RequirementId);
        var (_, foreignItem, _) = await AcceptedVoucherAsync("006-26");
        var partial = await _harness.Releases.ReleaseToMultipleAsync(new MultiRecipientReleaseRequest(voucherId, _harness.Clock.UtcNow, adjudication, [Part(item1, "COUNSEL, TEST A."), Part(foreignItem, "COUNSEL, TEST B.")]));
        Assert.False(partial.Succeeded);
        _harness.Db.ChangeTracker.Clear();
        Assert.Empty(_harness.Db.TemporaryReleases);
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item1)).AccountabilityStatus);
        Assert.Equal(0, (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!.AdditionalCopiesOut);

        var result = await _harness.Releases.ReleaseToMultipleAsync(new MultiRecipientReleaseRequest(voucherId, _harness.Clock.UtcNow.AddHours(-1), adjudication,
            [Part(item1, "COUNSEL, TEST A."), Part(item2, "COUNSEL, TEST B.")], null, "Two counsel, one form (TEST)"));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Value!.Count);
        Assert.Contains(result.Warnings, w => w.Contains("copies were made", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        var releases = await _harness.Releases.GetForVoucherAsync(voucherId);
        Assert.Equal(2, releases.Count);
        Assert.All(releases, r => { Assert.Equal(PaperCopyKind.AdditionalTemporaryReleaseCopy, r.PaperAccompanying); Assert.Equal(TemporaryReleaseStatus.Open, r.Status); Assert.Single(r.Items); });
        Assert.Equal(new[] { "COUNSEL, TEST A.", "COUNSEL, TEST B." }, releases.Select(r => r.ReceivedByDisplayName).OrderBy(n => n).ToArray());

        var paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(OriginalDisposition.HeldActive, paper.OriginalDisposition);
        Assert.Equal(RetainedPaperStatus.ActiveOriginal, paper.RetainedPaperStatus);
        Assert.Equal("ADJUDICATION", paper.FirstCopyContainerLabel);
        Assert.Equal(2, paper.AdditionalCopiesOut);
        Assert.True(paper.CopiesMadeNoted);
        var containers = await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId);
        Assert.Equal(1, containers.Single(c => c.Id == binder).VouchersFiled);
        Assert.Equal(1, containers.Single(c => c.Id == adjudication).VouchersFiled);

        // The original cannot go out as the original while copies are in use.
        var original = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.Equal("SUSP-001", original.RequirementId); // item 1 is out; and for a fresh item the paper rule would answer FIL-015
    }

    [Fact]
    public async Task AReturnWritesCustodyStatusAndPaper_AndTheLocationOnlyAsTheCustodianSays()
    {
        // AR 195-5 2-7b (SUSP-012), LOC-008: the returner -> custodian custody event, the status
        // back to the room, no automatic bin; the original back to its binder with the first copy.
        var (voucherId, item1, item2, binder, _, adjudication) = await ReadyAsync();
        await PlaceAsync(item1, _harness.ShelfBBin14Id);
        await PlaceAsync(item2, _harness.ShelfBBin19Id);
        var release = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1, item2]));
        Assert.True(release.Succeeded, release.Error);
        _harness.Clock.Advance(TimeSpan.FromDays(12));

        // Both a location and a confirmation is a contradiction; both attestations are required.
        var both = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item1, _harness.HighValueSafeId, true)], _harness.Clock.UtcNow, true, true));
        Assert.Equal("LOC-008", both.RequirementId);

        // A partial return: item 1 to a NEW bin; the original stays out with item 2.
        var returnedAt = _harness.Clock.UtcNow.AddHours(-2);
        var partial = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item1, _harness.HighValueSafeId)], returnedAt, true, true));
        Assert.True(partial.Succeeded, partial.Error);
        Assert.Contains(partial.Warnings, w => w.Contains("remain out", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        var view = (await _harness.Releases.GetAsync(release.Value))!;
        Assert.Equal(TemporaryReleaseStatus.Open, view.Status);
        Assert.Equal(1, view.ItemsOut);
        Assert.Equal(TemporaryReleaseItemStatus.Returned, view.Items.Single(i => i.EvidenceItemId == item1).Status);
        Assert.NotNull(view.Items.Single(i => i.EvidenceItemId == item1).ReturnCustodyEventId);
        Assert.False(view.OriginalAnnotatedOnReturnAttested); // not until the paper is back
        Assert.Equal(OriginalDisposition.AccompanyingTemporaryRelease, (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!.OriginalDisposition);

        var history1 = (await _harness.History.GetAsync(item1))!;
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, history1.AccountabilityStatus);
        Assert.Equal(_harness.HighValueSafeId, history1.CurrentLocationId);
        Assert.True(history1.ChainVerification.IsIntact);
        var back = await _harness.Db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().Where(c => c.EvidenceItemId == item1).OrderBy(c => c.SequenceNumber).LastAsync();
        Assert.Equal("COUNSEL, TEST B.", back.ReleasedBy.DisplayName);
        Assert.Equal(_harness.CustodianUserId, back.ReceivedBy.UserId);
        Assert.Equal(returnedAt, back.OccurredAtUtc);
        Assert.Equal(_harness.Clock.UtcNow, back.RecordedAtUtc);
        Assert.Equal(view.Items.Single(i => i.EvidenceItemId == item1).ReturnCustodyEventId, back.Id);

        // The last item: the paper attestations are required now; then no location at all is
        // allowed but flagged; the prior bin needs explicit confirmation.
        var noAttest = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item2, ConfirmReturnToPriorLocation: true)], _harness.Clock.UtcNow, true, false));
        Assert.Equal("SUSP-012", noAttest.RequirementId);
        _harness.Db.ChangeTracker.Clear();
        Assert.Equal(AccountabilityStatus.TemporarilyReleased, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item2)).AccountabilityStatus); // nothing written

        var final = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item2, ConfirmReturnToPriorLocation: true)], _harness.Clock.UtcNow, true, true));
        Assert.True(final.Succeeded, final.Error);
        Assert.DoesNotContain(final.Warnings, w => w.Contains("no location", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        view = (await _harness.Releases.GetAsync(release.Value))!;
        Assert.Equal(TemporaryReleaseStatus.Closed, view.Status);
        Assert.True(view.OriginalAnnotatedOnReturnAttested && view.FirstCopyChainAnnotatedOnReturnAttested);
        Assert.Equal(_harness.ShelfBBin19Id, (await _harness.History.GetAsync(item2))!.CurrentLocationId);
        var paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(OriginalDisposition.HeldActive, paper.OriginalDisposition);
        Assert.True(paper.SuspenseCopyFiledWithOriginal);
        var containers = await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId);
        Assert.Equal(1, containers.Single(c => c.Id == binder).VouchersFiled);
        Assert.Equal(0, containers.Single(c => c.Id == adjudication).VouchersFiled);

        // A closed release takes no more returns; an item back without a bin is flagged, not refused.
        Assert.Equal("SUSP-012", (await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item1)], _harness.Clock.UtcNow, true, true))).RequirementId);
    }

    [Fact]
    public async Task AnItemBackWithoutABinIsFlagged_AndAnInactivePriorBinIsRefused()
    {
        var (voucherId, item1, _, _, _, adjudication) = await ReadyAsync();
        await PlaceAsync(item1, _harness.ShelfBBin14Id);
        var release = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.True(release.Succeeded, release.Error);

        (await _harness.Db.StorageLocations.SingleAsync(l => l.Id == _harness.ShelfBBin14Id)).Deactivate();
        await _harness.Db.SaveChangesAsync();
        _harness.Db.ChangeTracker.Clear();

        var prior = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item1, ConfirmReturnToPriorLocation: true)], _harness.Clock.UtcNow, true, true));
        Assert.Equal("LOC-004", prior.RequirementId);

        var none = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(release.Value, [new ReturnedItem(item1)], _harness.Clock.UtcNow, true, true));
        Assert.True(none.Succeeded, none.Error);
        Assert.Contains(none.Warnings, w => w.Contains("no location recorded", StringComparison.Ordinal));
        _harness.Db.ChangeTracker.Clear();
        var history = (await _harness.History.GetAsync(item1))!;
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, history.AccountabilityStatus);
        Assert.Equal(_harness.ShelfBBin14Id, history.CurrentLocationId); // history keeps the last known bin; no new one was recorded
    }

    [Fact]
    public async Task AControlledSubstanceApparentChangeIsAnnotatedWithAnMfr_NotAfterALaboratoryRelease()
    {
        // AR 195-5 2-7d (SUSP-015).
        var (voucherId, item1, item2, _, usacil, adjudication) = await ReadyAsync();
        var trial = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.True(trial.Succeeded, trial.Error);
        // The first copy is in ADJUDICATION (with the trial release); a laboratory copy release names that folder (2-7b).
        var lab = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "USACIL (TEST)"), item2) with { PaperAccompanying = PaperCopyKind.AdditionalTemporaryReleaseCopy });
        Assert.True(lab.Succeeded, lab.Error);
        Assert.Equal(0, (await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == usacil).VouchersFiled);

        var noMfr = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(trial.Value, [new ReturnedItem(item1, ApparentChange: new("Weight reads 1.9 g against 2.1 g recorded (TEST)", ""))], _harness.Clock.UtcNow, true, true));
        Assert.Equal("SUSP-015", noMfr.RequirementId);

        var afterLab = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(lab.Value, [new ReturnedItem(item2, ApparentChange: new("Sample consumed (TEST)", "MFR TEST-0002"))], _harness.Clock.UtcNow, true, true));
        Assert.Equal("SUSP-015", afterLab.RequirementId);
        Assert.Contains("other than for laboratory examination", afterLab.Error, StringComparison.Ordinal);

        var annotated = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(trial.Value, [new ReturnedItem(item1, ApparentChange: new("Weight reads 1.9 g against 2.1 g recorded (TEST)", "MFR TEST-0003"))], _harness.Clock.UtcNow, true, true));
        Assert.True(annotated.Succeeded, annotated.Error);
        _harness.Db.ChangeTracker.Clear();
        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().Where(c => c.EvidenceItemId == item1).OrderBy(c => c.SequenceNumber).LastAsync();
        Assert.Contains("apparent change in controlled substance: Weight reads 1.9 g", custody.PurposeOfChangeOfCustody, StringComparison.Ordinal);
        Assert.Contains("2-7d MFR: MFR TEST-0003", custody.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyComesBackOntoTheFirstCopy_AndTheLaboratoryReturnMayNameTheMailNumber()
    {
        // AR 195-5 2-7b (copies), 2-7e (mail returned from the USACIL: the number in Released By).
        var (voucherId, item1, item2, binder, usacil, _) = await ReadyAsync();
        var first = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "USACIL (TEST)"), item1));
        Assert.True(first.Succeeded, first.Error);
        var second = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "USACIL (TEST)"), item2) with { PaperAccompanying = PaperCopyKind.AdditionalTemporaryReleaseCopy });
        Assert.True(second.Succeeded, second.Error);

        var mail = new ReleaseRecipient(CustodyPartyKind.AccountableMailNumber, "RB 000 000 001 US", AccountableMailNumber: "RB 000 000 001 US", Carrier: "USPS registered");
        var copyBack = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(second.Value, [new ReturnedItem(item2)], _harness.Clock.UtcNow, true, true, ReturnedBy: mail));
        Assert.True(copyBack.Succeeded, copyBack.Error);
        _harness.Db.ChangeTracker.Clear();
        var paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(0, paper.AdditionalCopiesOut);
        Assert.Equal(OriginalDisposition.AccompanyingTemporaryRelease, paper.OriginalDisposition); // the original is still with item 1
        Assert.Equal("USACIL", paper.FirstCopyContainerLabel);
        var custody = await _harness.Db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().Where(c => c.EvidenceItemId == item2).OrderBy(c => c.SequenceNumber).LastAsync();
        Assert.Equal(CustodyPartyKind.AccountableMailNumber, custody.ReleasedBy.Kind);
        Assert.Equal("RB 000 000 001 US", custody.ReleasedBy.AccountableMailNumber);

        var originalBack = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(first.Value, [new ReturnedItem(item1)], _harness.Clock.UtcNow, true, true));
        Assert.True(originalBack.Succeeded, originalBack.Error);
        _harness.Db.ChangeTracker.Clear();
        paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(OriginalDisposition.HeldActive, paper.OriginalDisposition);
        Assert.Null(paper.FirstCopyContainerLabel);
        Assert.True(paper.SuspenseCopyFiledWithOriginal);
        Assert.Equal(1, (await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == binder).VouchersFiled);

        // A mail number cannot return evidence from a legal proceeding.
        var (v2, i3, _, _, _, adj2) = await ReadyAsync("007-26", " (2)");
        var trial = await _harness.Releases.ReleaseAsync(Request(v2, adj2, itemIds: [i3]));
        var wrong = await _harness.Releases.ReturnAsync(new ReturnFromTemporaryReleaseRequest(trial.Value, [new ReturnedItem(i3)], _harness.Clock.UtcNow, true, true, ReturnedBy: mail));
        Assert.Equal("COC-006", wrong.RequirementId);
    }

    [Fact]
    public async Task ALaboratoryReleaseNamesItsLaboratory_NonUsacilNeedsCoordination_DftTakesACopy()
    {
        // AR 195-5 2-7c(1), 2-7c(2) (SUSP-013, SUSP-014).
        var (voucherId, item1, item2, _, usacil, _) = await ReadyAsync();
        var org = new ReleaseRecipient(CustodyPartyKind.Organization, "USACIL (TEST)");

        var noLab = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, org, item1) with { Laboratory = null });
        Assert.Equal("SUSP-013", noLab.RequirementId);

        var other = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "State Crime Laboratory (TEST)"), item1) with { Laboratory = new LaboratoryDetails("State Crime Laboratory (TEST)") });
        Assert.Equal("SUSP-013", other.RequirementId);
        Assert.Contains("prior coordination with the USACIL", other.Error, StringComparison.Ordinal);

        var dftOriginal = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "AFMES DFT"), item1) with { Laboratory = new LaboratoryDetails("AFMES DFT", CoordinatedWithUsacilAttested: true) });
        Assert.Equal("SUSP-014", dftOriginal.RequirementId);

        var dft = await _harness.Releases.ReleaseAsync(Request(voucherId, usacil, SuspenseCategory.Usacil, new ReleaseRecipient(CustodyPartyKind.Organization, "AFMES DFT"), item1) with
        {
            Laboratory = new LaboratoryDetails("AFMES DFT", CoordinatedWithUsacilAttested: true, ShippingDocumentReference: "GBL TEST-0001"),
            PaperAccompanying = PaperCopyKind.AdditionalTemporaryReleaseCopy
        });
        Assert.True(dft.Succeeded, dft.Error);
        Assert.Contains(dft.Warnings, w => w.Contains("2-7c(2)", StringComparison.Ordinal) && w.Contains("not returned", StringComparison.Ordinal));
        Assert.Contains(dft.Warnings, w => w.Contains("2-7f", StringComparison.Ordinal));

        _harness.Db.ChangeTracker.Clear();
        var paper = (await _harness.PhysicalDocuments.GetForVoucherAsync(voucherId))!;
        Assert.Equal(OriginalDisposition.HeldActive, paper.OriginalDisposition); // the original never left
        Assert.Equal(1, paper.AdditionalCopiesOut);
        var view = (await _harness.Releases.GetAsync(dft.Value))!;
        Assert.Equal("AFMES DFT", view.LaboratoryName);
        Assert.True(view.LaboratoryCoordinatedWithUsacilAttested);
        Assert.Equal("GBL TEST-0001", view.ShippingDocumentReference);
        var exam = await _harness.Db.ItemEvents.OfType<ExaminationEvent>().AsNoTracking().SingleAsync(e => e.EvidenceItemId == item1);
        Assert.Equal("AFMES DFT", exam.Laboratory);

        // The specimens are consumed: accounted for without return, with an MFR; the release closes.
        var noMfr = await _harness.Releases.RecordNotReturnedAsync(new NotReturnedRequest(dft.Value, [item1], NotReturnedReason.ConsumedOrRetainedByLaboratory, _harness.Clock.UtcNow, "Consumed in toxicology (TEST)"));
        Assert.Equal("SUSP-016", noMfr.RequirementId);
        var consumed = await _harness.Releases.RecordNotReturnedAsync(new NotReturnedRequest(dft.Value, [item1], NotReturnedReason.ConsumedOrRetainedByLaboratory, _harness.Clock.UtcNow, "Consumed in toxicology (TEST)", "MFR TEST-0004"));
        Assert.True(consumed.Succeeded, consumed.Error);
        _harness.Db.ChangeTracker.Clear();
        Assert.Equal(TemporaryReleaseStatus.Closed, (await _harness.Releases.GetAsync(dft.Value))!.Status);
        Assert.Equal(AccountabilityStatus.DispositionPending, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item1)).AccountabilityStatus);
        Assert.Equal(TemporaryReleaseItemStatus.NotReturnedAccountedFor, (await _harness.Db.TemporaryReleaseItems.AsNoTracking().SingleAsync(t => t.EvidenceItemId == item1)).Status);
        // Item 2 was never released.
        Assert.Equal(AccountabilityStatus.InEvidenceRoom, (await _harness.Db.EvidenceItems.AsNoTracking().SingleAsync(i => i.Id == item2)).AccountabilityStatus);
    }

    [Fact]
    public async Task AnItemEnteredInTheRecordOfTrialIsFinalDisposition_TheReleaseCloses()
    {
        // AR 195-5 3-1a(4), 2-8e(4) (SUSP-016).
        var (voucherId, item1, _, _, usacil, adjudication) = await ReadyAsync();
        var trial = await _harness.Releases.ReleaseAsync(Request(voucherId, adjudication, itemIds: [item1]));
        Assert.True(trial.Succeeded, trial.Error);

        var wrongReason = await _harness.Releases.RecordNotReturnedAsync(new NotReturnedRequest(trial.Value, [item1], NotReturnedReason.ConsumedOrRetainedByLaboratory, _harness.Clock.UtcNow, "x", "MFR"));
        Assert.Equal("SUSP-016", wrongReason.RequirementId);

        var result = await _harness.Releases.RecordNotReturnedAsync(new NotReturnedRequest(trial.Value, [item1], NotReturnedReason.EnteredInRecordOfTrial, _harness.Clock.UtcNow, "US v. TEST, entered as prosecution exhibit (TEST)"));
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Warnings, w => w.Contains("2-4g(1)", StringComparison.Ordinal));
        _harness.Db.ChangeTracker.Clear();
        var view = (await _harness.Releases.GetAsync(trial.Value))!;
        Assert.Equal(TemporaryReleaseStatus.Closed, view.Status);
        Assert.Contains(view.Events, e => e.Kind == TemporaryReleaseEventKind.ItemAccountedForWithoutReturn);
        var status = await _harness.Db.ItemEvents.OfType<StatusEvent>().AsNoTracking().Where(e => e.EvidenceItemId == item1).OrderBy(e => e.SequenceNumber).LastAsync();
        Assert.Equal(AccountabilityStatus.DispositionPending, status.ToStatus);
        Assert.Contains("record of trial", status.Reason, StringComparison.Ordinal);
        Assert.Equal(0, (await _harness.PhysicalDocuments.GetContainersAsync(_harness.EvidenceRoomId)).Single(c => c.Id == usacil).VouchersFiled);
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
