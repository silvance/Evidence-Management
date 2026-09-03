using Emc.Domain.Common;
using Emc.Domain.Events;

namespace Emc.Domain.Cases;

/// <summary>
/// One numbered line of evidence on a DA Form 4137 — the primary unit of accountability.
///
/// The regulatory argument for item-level accountability (docs/domain-model.md §1):
///   2-2f       each item is sealed in its own separate container; items under separate numbers
///              are not sealed together;
///   2-1b       one DA Form 4002 tag per item or container;
///   2-5b(1)(d) "When a DA Form 4137 contains several items that are not disposed of on the same
///              date, the date of disposition for each item will be shown opposite the items
///              description";
///   2-4h       the voucher becomes inactive only after ALL its items are disposed;
///   2-13b      a long-term retention certificate identifies contents by document number "and by
///              citing the absence of specific item numbers".
///
/// An item is the NUMBERED LINE, whose quantity may exceed one: 2-1b permits grouped items
/// ("a box containing tools") to be listed as one item with one DA Form 4002 (ITEM-010, DEC-04).
/// </summary>
public class EvidenceItem : Entity, IConcurrencyStamped
{
    /// <summary>
    /// AR 195-5 2-3d — descriptions "will include only descriptive information and not include
    /// phrases based on supposition or suspicions". The regulation's own examples are "suspected
    /// to be marijuana" and "believed to have been used to gain entry into the room".
    ///
    /// EMC WARNS rather than blocks: the regulation prohibits supposition, but a bare word list
    /// cannot reliably tell a prohibited inference from a legitimate description, and blocking a
    /// custodian's accurate description on a keyword match would be worse than the problem.
    /// </summary>
    public static readonly IReadOnlyList<string> SuppositionPhrases =
    [
        "suspected", "believed", "appears to be", "possibly", "probably",
        "thought to be", "alleged", "presumed", "assumed"
    ];

    private readonly List<ItemEvent> _events = [];

    private EvidenceItem() { }

    internal EvidenceItem(
        EvidenceVoucher voucher,
        int itemNumber,
        string description,
        string? quantity,
        string? serialNumber,
        string? uniqueDeviceIdentifier,
        bool isPossibleBiohazard,
        bool isFungible,
        bool isSealed,
        string? sealDescription)
    {
        ArgumentNullException.ThrowIfNull(voucher);

        // The back-reference is set here rather than being left to EF fixup, because IsLastItem
        // and the draft-editing guard both depend on it being present the moment the item exists.
        Voucher = voucher;
        VoucherId = voucher.Id;
        ItemNumber = Guard.Positive(itemNumber, "ITEM-002", "Item number");
        Description = Guard.NotBlank(description, "ITEM-003", "Description of articles");
        Quantity = Guard.TrimToNull(quantity);
        SerialNumber = Guard.TrimToNull(serialNumber);
        UniqueDeviceIdentifier = Guard.TrimToNull(uniqueDeviceIdentifier);
        IsPossibleBiohazard = isPossibleBiohazard;
        IsFungible = isFungible;
        IsSealed = isSealed;
        SealDescription = Guard.TrimToNull(sealDescription);
        AccountabilityStatus = AccountabilityStatus.Draft;
        ConcurrencyStamp = Guid.NewGuid();

        ValidateSealAnnotation();
    }

    public int VoucherId { get; private set; }
    public EvidenceVoucher? Voucher { get; private set; }

    /// <summary>Unique within the voucher, contiguous from 1 (AR 195-5 2-3d, invariant I-01).</summary>
    public int ItemNumber { get; private set; }

    /// <summary>
    /// AR 195-5 2-3d — describes the item accurately, individualizing it to the exclusion of any
    /// other item, limited to permanent characteristics, descriptive information only.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// AR 195-5 2-3d — "large numbers or weight should be given in approximations (for example,
    /// approximately 100 tablets)". Free text because the regulation's own form is free text.
    /// </summary>
    public string? Quantity { get; private set; }

    /// <summary>AR 195-5 2-3d — "If serial numbers are available for an item of evidence, they will be recorded".</summary>
    public string? SerialNumber { get; private set; }

    /// <summary>IMEI or comparable unique device identifier. A first-class field (ITEM-012).</summary>
    public string? UniqueDeviceIdentifier { get; private set; }

    /// <summary>
    /// AR 195-5 2-3l — the Description of Articles section "will reflect POSSIBLE BIOHAZARD in
    /// all capital letters after each item containing suspected blood or bodily fluids". EMC
    /// stores the flag and derives the annotation on render, so it can never drift (ITEM-007).
    /// </summary>
    public bool IsPossibleBiohazard { get; private set; }

    /// <summary>AR 195-5 glossary — fungible evidence is not readily identified or distinctively marked.</summary>
    public bool IsFungible { get; private set; }

    /// <summary>AR 195-5 2-2a, 2-2f.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>
    /// AR 195-5 2-3c — "the Description of Articles section ... should be annotated to reflect
    /// the sealing (for example, sealed in a paper sack which was marked for identification)".
    /// </summary>
    public string? SealDescription { get; private set; }

    /// <summary>
    /// AR 195-5 2-3d — when funds are seized or kept for safekeeping "the exact amount, by
    /// denomination, will be recorded on the DA Form 4137" (ITEM-006).
    /// </summary>
    public bool IsCurrency { get; private set; }

    public string? CurrencyDenominationBreakdown { get; private set; }
    public decimal? CurrencyTotalAmount { get; private set; }

    public AccountabilityStatus AccountabilityStatus { get; private set; }

    /// <summary>Highest event sequence issued for this item. Backs the per-item hash chain (I-07).</summary>
    public int LastEventSequenceNumber { get; private set; }

    /// <summary>
    /// Hash of the most recent event in this item's chain - the chain head (AUD-008).
    ///
    /// Held on the item rather than looked up per append, so that several events appended in one
    /// unit of work chain to each other correctly. Querying the database for the previous hash
    /// would return the last PERSISTED event and silently break the chain whenever more than one
    /// event is recorded before SaveChanges - which is the normal case during intake.
    /// </summary>
    public string? LastEventHash { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyList<ItemEvent> Events => _events.AsReadOnly();

    /// <summary>
    /// AR 195-5 2-3d — "The words LAST ITEM will be placed in capital letters after the last
    /// listed item on the next line below that item." Derived from position, never a stored flag,
    /// so it cannot drift when items are added or removed (ITEM-008).
    /// </summary>
    public bool IsLastItem => Voucher is not null && Voucher.Items.Count > 0
        && Voucher.Items.OrderBy(i => i.ItemNumber).Last().ItemNumber == ItemNumber;

    /// <summary>The description as it must appear on the form, with the 2-3l annotation applied.</summary>
    public string DescriptionForForm
        => IsPossibleBiohazard ? $"{Description} POSSIBLE BIOHAZARD" : Description;

    /// <summary>
    /// Phrases in the description that look like supposition (AR 195-5 2-3d). A warning surfaced
    /// to the user, not a hard block — see <see cref="SuppositionPhrases"/>.
    /// </summary>
    public IReadOnlyList<string> DetectSuppositionPhrases()
        => SuppositionPhrases
            .Where(p => Description.Contains(p, StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal void Renumber(int itemNumber)
        => ItemNumber = Guard.Positive(itemNumber, "ITEM-002", "Item number");

    public void UpdateDetails(
        string description,
        string? quantity,
        string? serialNumber,
        string? uniqueDeviceIdentifier,
        bool isPossibleBiohazard,
        bool isFungible,
        bool isSealed,
        string? sealDescription)
    {
        RequireDraftVoucher();

        Description = Guard.NotBlank(description, "ITEM-003", "Description of articles");
        Quantity = Guard.TrimToNull(quantity);
        SerialNumber = Guard.TrimToNull(serialNumber);
        UniqueDeviceIdentifier = Guard.TrimToNull(uniqueDeviceIdentifier);
        IsPossibleBiohazard = isPossibleBiohazard;
        IsFungible = isFungible;
        IsSealed = isSealed;
        SealDescription = Guard.TrimToNull(sealDescription);

        ValidateSealAnnotation();
    }

    /// <summary>AR 195-5 2-3d — exact amount by denomination (ITEM-006).</summary>
    public void RecordAsCurrency(string denominationBreakdown, decimal totalAmount)
    {
        RequireDraftVoucher();

        IsCurrency = true;
        CurrencyDenominationBreakdown = Guard.NotBlank(
            denominationBreakdown, "ITEM-006", "Currency denomination breakdown");

        if (totalAmount <= 0m)
        {
            throw new DomainRuleViolationException(
                "ITEM-006", "AR 195-5 2-3d: the exact amount of seized funds must be recorded.");
        }

        CurrencyTotalAmount = totalAmount;
    }

    /// <summary>Appends an event and issues its per-item sequence number (invariant I-07).</summary>
    public TEvent AppendEvent<TEvent>(TEvent itemEvent)
        where TEvent : ItemEvent
    {
        ArgumentNullException.ThrowIfNull(itemEvent);

        // Invariant I-17: a terminal item accepts no further custody or location events. A
        // correction is always permitted, because the record of a terminal item can still contain
        // an error that AR 195-5 1-7c(3) requires to be corrected.
        if (EvidenceVoucher.IsTerminal(AccountabilityStatus) && itemEvent is not CorrectionEvent)
        {
            throw new DomainRuleViolationException(
                "ITEM-001",
                $"Item {ItemNumber} is in terminal state {AccountabilityStatus}. Only a correction "
                + "may be recorded against it.");
        }

        LastEventSequenceNumber++;
        itemEvent.AssignSequence(Id, LastEventSequenceNumber);
        EventHashChain.Seal(itemEvent, LastEventHash);
        LastEventHash = itemEvent.EventHash;
        _events.Add(itemEvent);
        return itemEvent;
    }

    /// <summary>
    /// Applies a workflow state transition. Legality is checked by
    /// <see cref="AccountabilityStateMachine"/>; the transition itself is recorded as a
    /// <see cref="StatusEvent"/> by the calling application service so that the change is
    /// auditable (invariant I-22).
    /// </summary>
    public void TransitionTo(AccountabilityStatus target)
    {
        if (!AccountabilityStateMachine.IsAllowed(AccountabilityStatus, target))
        {
            throw new DomainRuleViolationException(
                "ITEM-001",
                $"Transition {AccountabilityStatus} -> {target} is not permitted for an evidence item.");
        }

        AccountabilityStatus = target;
    }

    /// <summary>
    /// Current custody, derived from event history with corrections applied — never stored
    /// (COC-001). A corrected custody event still counts: correcting the recipient changes who
    /// holds the item, it does not erase the transfer.
    /// </summary>
    public EffectiveItemEvent? CurrentCustody => EffectiveHistory.LatestOf<CustodyEvent>(_events);

    /// <summary>The current custody recipient as the record now reads.</summary>
    public string? CurrentCustodyHolder
        => CurrentCustody?.EffectiveValueOf(nameof(CustodyEvent.ReceivedBy));

    /// <summary>
    /// The current custody recipient as a RESOLVABLE ROW, not as text. A correction to the
    /// recipient moves this too, so "who holds this item" never degrades into a name that matches
    /// no party record (COC-004, AUD-016).
    /// </summary>
    public int? CurrentCustodyHolderPartyId => CurrentCustody?.EffectiveReceivedByPartyId;

    /// <summary>
    /// Current physical location, derived from event history with corrections applied
    /// (LOC-001). Correcting a location updates it; it does not fall back to an earlier location
    /// or to nothing.
    /// </summary>
    public EffectiveItemEvent? CurrentLocation => EffectiveHistory.LatestOf<LocationEvent>(_events);

    /// <summary>The current storage location path as the record now reads.</summary>
    public string? CurrentLocationPath
        => CurrentLocation?.EffectiveValueOf(nameof(LocationEvent.StorageLocationPath));

    /// <summary>
    /// The current storage location as a RESOLVABLE ROW. Inventory and discrepancy work
    /// (AR 195-5 3-2, 3-3a) ask which items a given container holds; that question is answered
    /// through this identifier, so a correction must move it and not only the displayed path
    /// (AUD-016).
    /// </summary>
    public int? CurrentLocationId => CurrentLocation?.EffectiveStorageLocationId;

    /// <summary>
    /// Complete chronological history: every event kind in one ordered sequence, corrections
    /// included. AR 195-5's glossary defines chain of custody as "a chronological written record
    /// reflecting the release and receipt of evidence from initial acquisition until final
    /// disposition" — a sequence, which is what this is.
    /// </summary>
    public IReadOnlyList<ItemEvent> ChronologicalHistory
        => _events
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.SequenceNumber)
            .ToList();

    private void ValidateSealAnnotation()
    {
        // AR 195-5 2-3c — when evidence is sealed in a container the Description of Articles
        // section should be annotated to reflect the sealing (ITEM-009).
        if (IsSealed && string.IsNullOrWhiteSpace(SealDescription))
        {
            throw new DomainRuleViolationException(
                "ITEM-009",
                "AR 195-5 2-3c: when evidence is sealed in an evidence container, the Description "
                + "of Articles section must be annotated to reflect the sealing (for example, "
                + "\"sealed in a paper sack which was marked for identification\").");
        }
    }

    private void RequireDraftVoucher()
    {
        if (Voucher is not null && !Voucher.AllowsItemEditing)
        {
            throw new DomainRuleViolationException(
                "VCH-010",
                "AR 195-5 2-3g: items may only be edited while the voucher is a draft. Once "
                + "submitted for custodian intake, changes must be made through a correction so "
                + "the original entry remains readable.");
        }
    }
}
