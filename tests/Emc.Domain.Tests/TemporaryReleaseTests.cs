using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Identity;
using Emc.Domain.Suspense;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// The temporary-release aggregate (AR 195-5 2-7a, 2-7b, 2-7e, 2-4f(3)): what it refuses, what it
/// records, and how it closes. Requirements: SUSP-001, SUSP-002, SUSP-003, SUSP-005, SUSP-010, SUSP-011, COC-006.
/// </summary>
public class TemporaryReleaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 14, 0, 0, TimeSpan.Zero);

    private static CustodyParty Custodian()
    {
        var user = new User("S-1-5-21-TEST-CUSTODIAN", "custodian.test@example.test", "TESTER, CUSTODIAN A.");
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(user, 9);
        return CustodyParty.ForUser(user);
    }

    private static CustodyParty TrialCounsel(bool identification = true)
        => CustodyParty.ForExternalPerson("COUNSEL, TEST B.", "CPT", "Office of the Staff Judge Advocate, Fort Test", identification);

    private static PaperReleaseAttestations All() => new(true, true, true, true, true);

    private static CustodyEvent Custody(CustodyParty from, CustodyParty to)
        => new(from, to, "Presentation at trial (TEST)", T0, T0, 9, isScrcni: false);

    private static TemporaryRelease Open(SuspenseCategory category = SuspenseCategory.Adjudication, CustodyParty? to = null, PaperReleaseAttestations? attestations = null)
        => TemporaryRelease.Create(1, 7, category, Custodian(), to ?? TrialCounsel(), "Presentation at trial (TEST)", "Fort Test courtroom", T0, T0.AddHours(2), 9, null, attestations ?? All(), 11);

    [Fact]
    public void ThePendingDispositionApprovalFolderIsNotAReleaseOfEvidence()
    {
        // SUSP-002 / 2-4f(3)(c): the ORIGINAL goes out for approval; the evidence stays.
        var ex = Assert.Throws<DomainRuleViolationException>(() => Open(SuspenseCategory.PendingDispositionApproval));
        Assert.Equal("SUSP-002", ex.RequirementId);
        Assert.Contains("does not leave", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTwoDashSevenBAttestationIsRequiredForAPerson_AndNamedWhenMissing()
    {
        var ex = Assert.Throws<DomainRuleViolationException>(() => Open(attestations: new(true, true, false, true, true)));
        Assert.Equal("SUSP-011", ex.RequirementId);
        Assert.Contains("FIRST COPY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("signature", ex.Message, StringComparison.OrdinalIgnoreCase); // an attestation, never a signature (AUD-013)

        var noId = Assert.Throws<DomainRuleViolationException>(() => Open(to: TrialCounsel(identification: false)));
        Assert.Equal("SUSP-011", noId.RequirementId);
    }

    [Fact]
    public void AccountableMailStandsInTheReceivedByBlock_ForUsacilOnly()
    {
        // 2-7e. No counter attestations for mail; and mail is the laboratory path, not adjudication.
        var mail = CustodyParty.ForAccountableMailNumber("RA 000 000 000 US", "USPS registered");
        var release = TemporaryRelease.Create(1, 7, SuspenseCategory.Usacil, Custodian(), mail, "Forensic examination, USACIL (TEST)", "USACIL", T0, T0, 9, null, PaperReleaseAttestations.NoneForAccountableMail(), 11);
        Assert.Equal(CustodyPartyKind.AccountableMailNumber, release.ReceivedBy.Kind);

        var wrong = Assert.Throws<DomainRuleViolationException>(() => TemporaryRelease.Create(1, 7, SuspenseCategory.Adjudication, Custodian(), mail, "x", null, T0, T0, 9, null, PaperReleaseAttestations.NoneForAccountableMail(), 11));
        Assert.Equal("COC-006", wrong.RequirementId);
    }

    [Fact]
    public void TheCustodianCannotReleaseToThemself_OrToTheUnableToSignText()
    {
        var self = Assert.Throws<DomainRuleViolationException>(() => Open(to: Custodian()));
        Assert.Equal("SUSP-003", self.RequirementId);
        var na = Assert.Throws<DomainRuleViolationException>(() => Open(to: CustodyParty.CustodianUnableToSign()));
        Assert.Equal("SUSP-003", na.RequirementId);
    }

    [Fact]
    public void ItemsAreTiedToTheirCustodyEvents_AndTheReleaseNeedsAtLeastOne()
    {
        var release = Open();
        Assert.Throws<DomainRuleViolationException>(() => release.MarkReleased(9, T0, null));

        var custody = Custody(release.ReleasedBy, release.ReceivedBy);
        release.AddItem(101, 1, custody);
        Assert.Throws<DomainRuleViolationException>(() => release.AddItem(101, 1, custody)); // once
        release.MarkReleased(9, T0.AddHours(2), "TEST note");
        Assert.Throws<DomainRuleViolationException>(() => release.MarkReleased(9, T0, null)); // once

        Assert.Same(custody, release.Items.Single().ReleaseCustodyEvent);
        Assert.Equal(TemporaryReleaseItemStatus.Out, release.Items.Single().Status);
        Assert.Single(release.Events, e => e.Kind == TemporaryReleaseEventKind.Released);
        Assert.Equal(T0, release.Events.Single().OccurredAtUtc);
        Assert.Equal(T0.AddHours(2), release.Events.Single().RecordedAtUtc);
        Assert.True(release.IsOpen);
        Assert.Equal(1, release.ItemsOut);
    }

    [Fact]
    public void DaysOutIsACount_ContactsAreTheRecordOfTwoDashSevenA_AndAFollowUpDateIsLocal()
    {
        var release = Open();
        release.AddItem(101, 1, Custody(release.ReleasedBy, release.ReceivedBy));
        release.MarkReleased(9, T0, null);

        Assert.Equal(0, release.DaysOut(T0.AddHours(23)));
        Assert.Equal(94, release.DaysOut(T0.AddDays(94).AddMinutes(5)));
        Assert.Null(release.LastContactAtUtc);
        Assert.Null(release.ExpectedFollowUpLocal);

        var contact = release.RecordContact(T0.AddDays(30), T0.AddDays(30).AddHours(1), 9, ContactMethod.Telephone, "COUNSEL, TEST B.", ContactOutcome.EvidenceStillRequired, "Trial continued (TEST).", T0.AddDays(60));
        Assert.Equal(T0.AddDays(30), release.LastContactAtUtc);
        Assert.Equal(T0.AddDays(60), release.ExpectedFollowUpLocal);
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(SuspenseContact)));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(TemporaryReleaseEvent)));
        Assert.False(typeof(IAppendOnly).IsAssignableFrom(typeof(TemporaryRelease)));
        Assert.Equal(ContactOutcome.EvidenceStillRequired, contact.Outcome);

        // Nothing on the aggregate is a deadline.
        Assert.DoesNotContain(typeof(TemporaryRelease).GetProperties(), p => p.Name.Contains("Deadline", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Overdue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReturnsCloseTheReleaseWhenNothingIsOut_AndAClosedReleaseTakesNoMore()
    {
        var release = Open();
        release.AddItem(101, 1, Custody(release.ReleasedBy, release.ReceivedBy));
        release.AddItem(102, 2, Custody(release.ReleasedBy, release.ReceivedBy));
        release.MarkReleased(9, T0, null);

        var back = Custody(release.ReceivedBy, release.ReleasedBy);
        release.RecordItemReturned(101, back, T0.AddDays(10), T0.AddDays(10), 9, null);
        Assert.True(release.IsOpen);
        Assert.Equal(1, release.ItemsOut);
        Assert.Same(back, release.Items.Single(i => i.EvidenceItemId == 101).ReturnCustodyEvent);
        Assert.Throws<DomainRuleViolationException>(() => release.RecordItemReturned(101, back, T0.AddDays(11), T0.AddDays(11), 9, null)); // not out
        Assert.Throws<DomainRuleViolationException>(() => release.RecordItemReturned(999, back, T0.AddDays(11), T0.AddDays(11), 9, null)); // not on it

        release.RecordItemAccountedForWithoutReturn(102, T0.AddDays(20), T0.AddDays(20), 9, "Entered as a permanent part of the record of trial (2-8e(4)) (TEST).");
        Assert.False(release.IsOpen);
        Assert.Equal(TemporaryReleaseStatus.Closed, release.Status);
        Assert.Equal(T0.AddDays(20), release.ClosedAtUtc);
        Assert.Contains(release.Events, e => e.Kind == TemporaryReleaseEventKind.Closed);

        var closed = Assert.Throws<DomainRuleViolationException>(() => release.RecordContact(T0.AddDays(21), T0.AddDays(21), 9, ContactMethod.Email, "x", ContactOutcome.Other, null, null));
        Assert.Equal("SUSP-005", closed.RequirementId);
    }
}
