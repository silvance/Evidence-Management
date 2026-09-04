using Emc.Domain.Common;
using Emc.Domain.Events;

namespace Emc.Domain.Suspense;

/// <summary>
/// AR 195-5 2-4f(3): the evidence room's three suspense folders, by the regulation's own names.
/// USACIL - evidence sent to the laboratory; ADJUDICATION - evidence on temporary release for
/// legal proceedings; PENDING DISPOSITION APPROVAL - the ORIGINAL form is with trial counsel or
/// the prosecutor for disposition approval while the evidence stays in the room. No fourth
/// category exists in the regulation and none is invented here (SUSP-002).
/// </summary>
public enum SuspenseCategory
{
    Usacil = 1,
    Adjudication = 2,
    PendingDispositionApproval = 3
}

public enum TemporaryReleaseStatus
{
    /// <summary>At least one item is still out.</summary>
    Open = 1,

    /// <summary>Every item has come back, or has been accounted for without return (record of trial, consumed at the laboratory).</summary>
    Closed = 2
}

public enum TemporaryReleaseItemStatus
{
    Out = 1,
    Returned = 2,

    /// <summary>Accounted for without coming back: entered in the record of trial (2-8e(4), 3-1a(4)) or consumed / retained at the laboratory (2-7c(2)). The item's own disposition is recorded on the item.</summary>
    NotReturnedAccountedFor = 3
}

public enum TemporaryReleaseEventKind
{
    Released = 1,
    ItemReturned = 2,
    ItemAccountedForWithoutReturn = 3,
    Note = 4,
    Closed = 5
}

/// <summary>AR 195-5 2-7a - how the custodian kept "reasonable and adequate contact".</summary>
public enum ContactMethod
{
    InPerson = 1,
    Telephone = 2,
    Email = 3,
    Mail = 4,
    Other = 5
}

public enum ContactOutcome
{
    EvidenceStillRequired = 1,
    ReturnArranged = 2,
    NoResponse = 3,
    EnteredInRecordOfTrial = 4,
    ConsumedOrRetainedByLaboratory = 5,
    Other = 6
}

/// <summary>
/// One temporary release of evidence from this room (AR 195-5 2-7a, 2-7b): which items, to whom,
/// for what, when, into which suspense folder the first copy went, and what the custodian
/// attested the paper shows. Item-level: the members are the items released; each is tied to the
/// custody event that recorded the release and, later, the one that recorded the return.
///
/// What this is NOT: a storage location (LOC-005), a signature (AUD-013), or a deadline
/// (SUSP-004). The regulation sets no number of days; it requires "reasonable and adequate
/// contact" (2-7a) and that the evidence not be out "for an excessive period" (2-7b, 3-1a(4)).
/// The contact history is the record that the obligation was met (SUSP-005). Days out is a
/// count; the threshold it is compared with is a LOCAL management threshold.
///
/// Requirements: SUSP-001, SUSP-002, SUSP-003, SUSP-005, SUSP-010, SUSP-011.
/// </summary>
public sealed class TemporaryRelease : Entity, IConcurrencyStamped
{
    private readonly List<TemporaryReleaseItem> _items = [];
    private readonly List<TemporaryReleaseEvent> _events = [];
    private readonly List<SuspenseContact> _contacts = [];

    private TemporaryRelease() { }

    private TemporaryRelease(
        int voucherId, int evidenceRoomId, SuspenseCategory category,
        CustodyParty releasedBy, CustodyParty receivedBy,
        string purpose, string? destination, DateTimeOffset releasedAtLocal, DateTimeOffset recordedAtUtc, int recordedByUserId,
        DateTimeOffset? expectedFollowUpLocal, PaperReleaseAttestations attestations, int suspenseFolderContainerId, string? notes)
    {
        VoucherId = Guard.Positive(voucherId, "SUSP-001", "Voucher");
        EvidenceRoomId = Guard.Positive(evidenceRoomId, "SUSP-001", "Evidence room");
        Category = category;
        ReleasedBy = releasedBy;
        ReceivedBy = receivedBy;
        Purpose = Guard.NotBlank(purpose, "SUSP-003", "Purpose of the release");
        Destination = Guard.TrimToNull(destination);
        var released = AccountabilityTime.Normalize(releasedAtLocal);
        ReleasedAtUtc = released.ToUniversalTime();
        ReleasedAtLocal = released;
        RecordedAtUtc = AccountabilityTime.Normalize(recordedAtUtc);
        RecordedByUserId = Guard.Positive(recordedByUserId, "SUSP-003", "Recording user");
        ExpectedFollowUpLocal = expectedFollowUpLocal is { } f ? AccountabilityTime.Normalize(f) : null;
        Attestations = attestations;
        SuspenseFolderContainerId = Guard.Positive(suspenseFolderContainerId, "SUSP-007", "Suspense folder");
        Notes = Guard.TrimToNull(notes);
        Status = TemporaryReleaseStatus.Open;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>
    /// Creates the release. Refuses: a PENDING DISPOSITION APPROVAL "release" (the evidence does
    /// not leave; that folder holds the copy while the ORIGINAL is out for approval - a paper
    /// action, not a custody one); an empty item list; a recipient whose paper attestations are
    /// incomplete (2-7b) unless the recipient is an accountable mail number (2-7e, where the
    /// number stands in the Received By block and no one signs at the custodian's counter).
    /// </summary>
    public static TemporaryRelease Create(
        int voucherId, int evidenceRoomId, SuspenseCategory category,
        CustodyParty releasedBy, CustodyParty receivedBy,
        string purpose, string? destination, DateTimeOffset releasedAtLocal, DateTimeOffset recordedAtUtc, int recordedByUserId,
        DateTimeOffset? expectedFollowUpLocal, PaperReleaseAttestations attestations, int suspenseFolderContainerId, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(releasedBy);
        ArgumentNullException.ThrowIfNull(receivedBy);
        ArgumentNullException.ThrowIfNull(attestations);

        if (category == SuspenseCategory.PendingDispositionApproval)
        {
            throw new DomainRuleViolationException(
                "SUSP-002",
                "AR 195-5 para 2-4f(3)(c): the PENDING DISPOSITION APPROVAL folder holds the suspense copy while the "
                + "ORIGINAL DA Form 4137 is with trial counsel or the prosecutor for disposition approval. The evidence "
                + "does not leave the room. Record that as the paper action \"send the original for disposition "
                + "approval\"; a temporary release of evidence is USACIL or ADJUDICATION.");
        }

        if (receivedBy.Kind == CustodyPartyKind.CustodianUnableToSign)
        {
            throw new DomainRuleViolationException("SUSP-003", "\"N/A Custodian Unable to Sign\" is a Released By entry (3-2g(5)), never a recipient.");
        }

        if (receivedBy.Kind == CustodyPartyKind.InternalUser && receivedBy.UserId == releasedBy.UserId && receivedBy.UserId is not null)
        {
            throw new DomainRuleViolationException("SUSP-003", "The custodian cannot temporarily release evidence to themself.");
        }

        if (receivedBy.Kind != CustodyPartyKind.AccountableMailNumber)
        {
            attestations.RequireCompleteForPersonOrOrganization();
        }
        else if (category != SuspenseCategory.Usacil)
        {
            throw new DomainRuleViolationException(
                "COC-006", "AR 195-5 para 2-7e: an accountable mail number stands in the Received By block for evidence MAILED TO THE USACIL. For any other release a person or organization receives the evidence and signs for it.");
        }

        if (receivedBy.Kind == CustodyPartyKind.ExternalPerson && !receivedBy.IdentificationVerified)
        {
            throw new DomainRuleViolationException(
                "SUSP-011", "AR 195-5 para 2-7b: a person receiving evidence will present appropriate identification. Record that identification was presented.");
        }

        return new TemporaryRelease(voucherId, evidenceRoomId, category, releasedBy, receivedBy, purpose, destination, releasedAtLocal, recordedAtUtc, recordedByUserId,
            expectedFollowUpLocal, attestations, suspenseFolderContainerId, notes);
    }

    public int VoucherId { get; private set; }
    public int EvidenceRoomId { get; private set; }
    public SuspenseCategory Category { get; private set; }

    public int ReleasedByPartyId { get; private set; }
    public CustodyParty ReleasedBy { get; private set; } = null!;

    public int ReceivedByPartyId { get; private set; }
    public CustodyParty ReceivedBy { get; private set; } = null!;

    /// <summary>The "Purpose of Change of Custody" text (2-3f), e.g. "Forensic examination, USACIL" or "Presentation at trial, US v. TEST".</summary>
    public string Purpose { get; private set; } = string.Empty;
    public string? Destination { get; private set; }

    /// <summary>When the evidence left the room - what the form says - in UTC and as written locally (AUD-011).</summary>
    public DateTimeOffset ReleasedAtUtc { get; private set; }
    public DateTimeOffset ReleasedAtLocal { get; private set; }

    /// <summary>When EMC learned of it. Distinct from <see cref="ReleasedAtUtc"/>: back-dated entry is legitimate.</summary>
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public int RecordedByUserId { get; private set; }

    /// <summary>A LOCAL follow-up date the custodian chose (SUSP-004). Not a regulatory deadline; the regulation sets none.</summary>
    public DateTimeOffset? ExpectedFollowUpLocal { get; private set; }

    public PaperReleaseAttestations Attestations { get; private set; } = null!;

    /// <summary>The 2-4f(3) folder the first copy went into. The folder's kind must match the category.</summary>
    public int SuspenseFolderContainerId { get; private set; }

    public string? Notes { get; private set; }
    public TemporaryReleaseStatus Status { get; private set; }
    public DateTimeOffset? ClosedAtUtc { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyList<TemporaryReleaseItem> Items => _items.AsReadOnly();
    public IReadOnlyList<TemporaryReleaseEvent> Events => _events.AsReadOnly();
    public IReadOnlyList<SuspenseContact> Contacts => _contacts.AsReadOnly();

    public bool IsOpen => Status == TemporaryReleaseStatus.Open;
    public int ItemsOut => _items.Count(i => i.Status == TemporaryReleaseItemStatus.Out);

    /// <summary>Calendar days since the evidence left, for an open release. A count, never a deadline (SUSP-004).</summary>
    public int DaysOut(DateTimeOffset now) => Math.Max(0, (int)Math.Floor((now.ToUniversalTime() - ReleasedAtUtc).TotalDays));

    public DateTimeOffset? LastContactAtUtc => _contacts.Count == 0 ? null : _contacts.Max(c => c.ContactedAtUtc);

    /// <summary>Adds an item to the release, tied to the custody event that recorded it leaving (saved together). An item may be on the release once.</summary>
    public TemporaryReleaseItem AddItem(int evidenceItemId, int itemNumber, CustodyEvent releaseCustodyEvent)
    {
        if (_items.Any(i => i.EvidenceItemId == evidenceItemId))
        {
            throw new DomainRuleViolationException("SUSP-001", $"Item {itemNumber} is already on this release.");
        }

        var item = new TemporaryReleaseItem(this, evidenceItemId, itemNumber, releaseCustodyEvent);
        _items.Add(item);
        return item;
    }

    /// <summary>Records the release event once the items are on it. Called by the orchestrating service after the custody events exist.</summary>
    public void MarkReleased(int recordedByUserId, DateTimeOffset recordedAtUtc, string? narrative)
    {
        if (_items.Count == 0)
        {
            throw new DomainRuleViolationException("SUSP-001", "A temporary release names at least one item.");
        }

        if (_events.Any(e => e.Kind == TemporaryReleaseEventKind.Released))
        {
            throw new DomainRuleViolationException("SUSP-001", "The release has already been recorded.");
        }

        _events.Add(new TemporaryReleaseEvent(this, TemporaryReleaseEventKind.Released, ReleasedAtUtc, recordedAtUtc, recordedByUserId, null, narrative));
    }

    /// <summary>AR 195-5 2-7a. Append-only: a contact is a fact about what was done; nothing here is edited.</summary>
    public SuspenseContact RecordContact(DateTimeOffset contactedAtLocal, DateTimeOffset recordedAtUtc, int recordedByUserId, ContactMethod method, string contactedPerson, ContactOutcome outcome, string? narrative, DateTimeOffset? nextFollowUpLocal)
    {
        RequireOpen("SUSP-005", "record a contact on");
        var contact = new SuspenseContact(this, contactedAtLocal, recordedAtUtc, recordedByUserId, method, contactedPerson, outcome, narrative, nextFollowUpLocal);
        _contacts.Add(contact);
        if (nextFollowUpLocal is { } next)
        {
            ExpectedFollowUpLocal = AccountabilityTime.Normalize(next);
        }

        ConcurrencyStamp = Guid.NewGuid();
        return contact;
    }

    /// <summary>AR 195-5 2-7b: an item came back. Tied to the custody event recording the return. Closes the release when nothing is left out.</summary>
    public void RecordItemReturned(int evidenceItemId, CustodyEvent returnCustodyEvent, DateTimeOffset returnedAtLocal, DateTimeOffset recordedAtUtc, int recordedByUserId, string? narrative)
    {
        RequireOpen("SUSP-010", "record a return on");
        var item = _items.FirstOrDefault(i => i.EvidenceItemId == evidenceItemId)
            ?? throw new DomainRuleViolationException("SUSP-010", "That item is not on this release.");
        item.MarkReturned(returnCustodyEvent, returnedAtLocal);
        _events.Add(new TemporaryReleaseEvent(this, TemporaryReleaseEventKind.ItemReturned, AccountabilityTime.Normalize(returnedAtLocal).ToUniversalTime(), recordedAtUtc, recordedByUserId, evidenceItemId, narrative));
        CloseIfNothingOut(recordedAtUtc, recordedByUserId);
    }

    /// <summary>An item accounted for without coming back (record of trial 2-8e(4); consumed or retained at the laboratory 2-7c(2)). The item's own status change is recorded on the item.</summary>
    public void RecordItemAccountedForWithoutReturn(int evidenceItemId, DateTimeOffset occurredAtLocal, DateTimeOffset recordedAtUtc, int recordedByUserId, string narrative)
    {
        RequireOpen("SUSP-010", "account for an item on");
        var item = _items.FirstOrDefault(i => i.EvidenceItemId == evidenceItemId)
            ?? throw new DomainRuleViolationException("SUSP-010", "That item is not on this release.");
        item.MarkAccountedForWithoutReturn(occurredAtLocal);
        _events.Add(new TemporaryReleaseEvent(this, TemporaryReleaseEventKind.ItemAccountedForWithoutReturn, AccountabilityTime.Normalize(occurredAtLocal).ToUniversalTime(), recordedAtUtc, recordedByUserId, evidenceItemId,
            Guard.NotBlank(narrative, "SUSP-010", "Narrative")));
        CloseIfNothingOut(recordedAtUtc, recordedByUserId);
    }

    public void AddNote(DateTimeOffset recordedAtUtc, int recordedByUserId, string narrative)
    {
        _events.Add(new TemporaryReleaseEvent(this, TemporaryReleaseEventKind.Note, recordedAtUtc, recordedAtUtc, recordedByUserId, null, Guard.NotBlank(narrative, "SUSP-003", "Note")));
        ConcurrencyStamp = Guid.NewGuid();
    }

    private void CloseIfNothingOut(DateTimeOffset recordedAtUtc, int recordedByUserId)
    {
        ConcurrencyStamp = Guid.NewGuid();
        if (ItemsOut > 0)
        {
            return;
        }

        Status = TemporaryReleaseStatus.Closed;
        ClosedAtUtc = AccountabilityTime.Normalize(recordedAtUtc);
        _events.Add(new TemporaryReleaseEvent(this, TemporaryReleaseEventKind.Closed, ClosedAtUtc.Value, recordedAtUtc, recordedByUserId, null, null));
    }

    private void RequireOpen(string requirementId, string verb)
    {
        if (!IsOpen)
        {
            throw new DomainRuleViolationException(requirementId, $"This temporary release is closed; you cannot {verb} it.");
        }
    }
}

/// <summary>
/// What the custodian attests the PAPER shows for this release (AR 195-5 2-7b): the recipient
/// physically inventoried the evidence; signed the Received By column on the ORIGINAL; signed it
/// on the FIRST COPY; presented identification; and was informed of the safeguarding, chain of
/// custody and return obligations. Each is a record that a paper act occurred - never a
/// signature and never described as one (AUD-013). An owned value; immutable once recorded.
/// </summary>
public sealed class PaperReleaseAttestations
{
    private PaperReleaseAttestations() { }

    public PaperReleaseAttestations(
        bool physicalInventoryPerformedAttested,
        bool original4137ReceivedBySignedAttested,
        bool firstCopyReceivedBySignedAttested,
        bool identificationPresentedAttested,
        bool obligationsInformedAttested)
    {
        PhysicalInventoryPerformedAttested = physicalInventoryPerformedAttested;
        Original4137ReceivedBySignedAttested = original4137ReceivedBySignedAttested;
        FirstCopyReceivedBySignedAttested = firstCopyReceivedBySignedAttested;
        IdentificationPresentedAttested = identificationPresentedAttested;
        ObligationsInformedAttested = obligationsInformedAttested;
    }

    /// <summary>2-7b: "will physically inventory the evidence".</summary>
    public bool PhysicalInventoryPerformedAttested { get; private set; }

    /// <summary>2-7b: "sign for it in the Received By column ... on the original".</summary>
    public bool Original4137ReceivedBySignedAttested { get; private set; }

    /// <summary>2-7b: "... and first copy of the DA Form 4137".</summary>
    public bool FirstCopyReceivedBySignedAttested { get; private set; }

    /// <summary>2-7b: "will present appropriate identification".</summary>
    public bool IdentificationPresentedAttested { get; private set; }

    /// <summary>2-7b: informed that the evidence must be safeguarded, the chain maintained, and the evidence returned as soon as no longer needed.</summary>
    public bool ObligationsInformedAttested { get; private set; }

    /// <summary>For a mailed release (2-7e) none of these are made at the counter; the mail number stands in the Received By block.</summary>
    public static PaperReleaseAttestations NoneForAccountableMail() => new(false, false, false, false, false);

    internal void RequireCompleteForPersonOrOrganization()
    {
        var missing = new List<string>();
        if (!PhysicalInventoryPerformedAttested) missing.Add("the recipient physically inventoried the evidence");
        if (!Original4137ReceivedBySignedAttested) missing.Add("the recipient signed the Received By column on the ORIGINAL DA Form 4137");
        if (!FirstCopyReceivedBySignedAttested) missing.Add("the recipient signed the Received By column on the FIRST COPY");
        if (!IdentificationPresentedAttested) missing.Add("the recipient presented appropriate identification");
        if (!ObligationsInformedAttested) missing.Add("the recipient was informed of the safeguarding, chain-of-custody and return obligations");

        if (missing.Count > 0)
        {
            throw new DomainRuleViolationException(
                "SUSP-011",
                "AR 195-5 para 2-7b requires each of the following before evidence is temporarily released, and this record attests that they occurred on paper: "
                + string.Join("; ", missing) + ".");
        }
    }
}

/// <summary>One item on a temporary release, tied to the custody events that took it out and brought it back.</summary>
public sealed class TemporaryReleaseItem : Entity
{
    private TemporaryReleaseItem() { }

    internal TemporaryReleaseItem(TemporaryRelease release, int evidenceItemId, int itemNumber, CustodyEvent releaseCustodyEvent)
    {
        ArgumentNullException.ThrowIfNull(releaseCustodyEvent);
        Release = release;
        TemporaryReleaseId = release.Id;
        EvidenceItemId = Guard.Positive(evidenceItemId, "SUSP-001", "Evidence item");
        ItemNumber = Guard.Positive(itemNumber, "SUSP-001", "Item number");
        ReleaseCustodyEvent = releaseCustodyEvent;
        ReleaseCustodyEventId = releaseCustodyEvent.Id;
        Status = TemporaryReleaseItemStatus.Out;
    }

    public int TemporaryReleaseId { get; private set; }
    public TemporaryRelease? Release { get; private set; }
    public int EvidenceItemId { get; private set; }
    public int ItemNumber { get; private set; }

    /// <summary>The CustodyEvent that recorded this item leaving (release correlation, COC-003).</summary>
    public int ReleaseCustodyEventId { get; private set; }
    public CustodyEvent? ReleaseCustodyEvent { get; private set; }

    /// <summary>The CustodyEvent that recorded it coming back; null while out or when accounted for without return.</summary>
    public int? ReturnCustodyEventId { get; private set; }
    public CustodyEvent? ReturnCustodyEvent { get; private set; }
    public TemporaryReleaseItemStatus Status { get; private set; }
    public DateTimeOffset? ReturnedAtUtc { get; private set; }

    internal void MarkReturned(CustodyEvent returnCustodyEvent, DateTimeOffset returnedAtLocal)
    {
        ArgumentNullException.ThrowIfNull(returnCustodyEvent);
        if (Status != TemporaryReleaseItemStatus.Out)
        {
            throw new DomainRuleViolationException("SUSP-010", $"Item {ItemNumber} is not out on this release.");
        }

        ReturnCustodyEvent = returnCustodyEvent;
        ReturnCustodyEventId = returnCustodyEvent.Id;
        ReturnedAtUtc = AccountabilityTime.Normalize(returnedAtLocal).ToUniversalTime();
        Status = TemporaryReleaseItemStatus.Returned;
    }

    internal void MarkAccountedForWithoutReturn(DateTimeOffset occurredAtLocal)
    {
        if (Status != TemporaryReleaseItemStatus.Out)
        {
            throw new DomainRuleViolationException("SUSP-010", $"Item {ItemNumber} is not out on this release.");
        }

        ReturnedAtUtc = AccountabilityTime.Normalize(occurredAtLocal).ToUniversalTime();
        Status = TemporaryReleaseItemStatus.NotReturnedAccountedFor;
    }
}

/// <summary>What happened to the release, in order. Append-only (SQL trigger and SaveChanges guard).</summary>
public sealed class TemporaryReleaseEvent : Entity, IAppendOnly
{
    private TemporaryReleaseEvent() { }

    internal TemporaryReleaseEvent(TemporaryRelease release, TemporaryReleaseEventKind kind, DateTimeOffset occurredAtUtc, DateTimeOffset recordedAtUtc, int recordedByUserId, int? evidenceItemId, string? narrative)
    {
        Release = release;
        TemporaryReleaseId = release.Id;
        Kind = kind;
        OccurredAtUtc = AccountabilityTime.Normalize(occurredAtUtc).ToUniversalTime();
        RecordedAtUtc = AccountabilityTime.Normalize(recordedAtUtc);
        RecordedByUserId = Guard.Positive(recordedByUserId, "SUSP-003", "Recording user");
        EvidenceItemId = evidenceItemId;
        Narrative = Guard.TrimToNull(narrative);
    }

    public int TemporaryReleaseId { get; private set; }
    public TemporaryRelease? Release { get; private set; }
    public TemporaryReleaseEventKind Kind { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public int RecordedByUserId { get; private set; }
    public int? EvidenceItemId { get; private set; }
    public string? Narrative { get; private set; }
}

/// <summary>
/// AR 195-5 2-7a: one contact the custodian made with the person or agency holding the evidence.
/// The record that "reasonable and adequate contact" was maintained (SUSP-005). Append-only.
/// </summary>
public sealed class SuspenseContact : Entity, IAppendOnly
{
    private SuspenseContact() { }

    internal SuspenseContact(TemporaryRelease release, DateTimeOffset contactedAtLocal, DateTimeOffset recordedAtUtc, int recordedByUserId, ContactMethod method, string contactedPerson, ContactOutcome outcome, string? narrative, DateTimeOffset? nextFollowUpLocal)
    {
        Release = release;
        TemporaryReleaseId = release.Id;
        var contacted = AccountabilityTime.Normalize(contactedAtLocal);
        ContactedAtUtc = contacted.ToUniversalTime();
        ContactedAtLocal = contacted;
        RecordedAtUtc = AccountabilityTime.Normalize(recordedAtUtc);
        RecordedByUserId = Guard.Positive(recordedByUserId, "SUSP-005", "Recording user");
        Method = method;
        ContactedPerson = Guard.NotBlank(contactedPerson, "SUSP-005", "Person or office contacted");
        Outcome = outcome;
        Narrative = Guard.TrimToNull(narrative);
        NextFollowUpLocal = nextFollowUpLocal is { } n ? AccountabilityTime.Normalize(n) : null;
    }

    public int TemporaryReleaseId { get; private set; }
    public TemporaryRelease? Release { get; private set; }
    public DateTimeOffset ContactedAtUtc { get; private set; }
    public DateTimeOffset ContactedAtLocal { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public int RecordedByUserId { get; private set; }
    public ContactMethod Method { get; private set; }
    public string ContactedPerson { get; private set; } = string.Empty;
    public ContactOutcome Outcome { get; private set; }
    public string? Narrative { get; private set; }

    /// <summary>The custodian's own next follow-up date. LOCAL; not a regulatory deadline (SUSP-004).</summary>
    public DateTimeOffset? NextFollowUpLocal { get; private set; }
}
