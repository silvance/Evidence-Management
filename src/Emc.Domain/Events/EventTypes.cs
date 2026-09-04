using Emc.Domain.Common;

namespace Emc.Domain.Events;

/// <summary>
/// A change of custody.
///
/// AR 195-5 2-3f: "Any change in custody of evidence or safeguarded items, after the first
/// DALEO or Army CI agent acquires it, will be recorded in the Change of Custody section of the
/// DA Form 4137 ... When custody of sealed evidence is changed, the Purpose of Change of Custody
/// column will be noted with SCRCNI."
///
/// Current custody is DERIVED from the event history and never stored (COC-001). The AR 195-5
/// glossary defines chain of custody as "a chronological written record reflecting the release
/// and receipt of evidence from initial acquisition until final disposition" — a sequence.
///
/// Requirements: COC-002, COC-003, COC-005.
/// </summary>
public class CustodyEvent : ItemEvent
{
    /// <summary>AR 195-5 2-3e — sealed container received; contents not inventoried.</summary>
    public const string ScrcniAnnotation = "SCRCNI";

    private CustodyEvent() { }

    public CustodyEvent(
        CustodyParty releasedBy,
        CustodyParty receivedBy,
        string purposeOfChangeOfCustody,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        bool isScrcni,
        string? destination = null,
        string? agency = null,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, recordedByUserId, notes)
    {
        ArgumentNullException.ThrowIfNull(releasedBy);
        ArgumentNullException.ThrowIfNull(receivedBy);

        ReleasedBy = releasedBy;
        ReceivedBy = receivedBy;
        PurposeOfChangeOfCustody = Guard.NotBlank(
            purposeOfChangeOfCustody, "COC-003", "Purpose of change of custody");
        IsScrcni = isScrcni;
        Destination = Guard.TrimToNull(destination);
        Agency = Guard.TrimToNull(agency);
    }

    public override ItemEventKind Kind => ItemEventKind.Custody;

    public int ReleasedByPartyId { get; private set; }
    public CustodyParty ReleasedBy { get; private set; } = null!;

    public int ReceivedByPartyId { get; private set; }
    public CustodyParty ReceivedBy { get; private set; } = null!;

    /// <summary>AR 195-5 2-3f — the "Purpose of Change of Custody" column.</summary>
    public string PurposeOfChangeOfCustody { get; private set; } = string.Empty;

    /// <summary>
    /// AR 195-5 2-3e / 2-3f — sealed container received; contents not inventoried. Rendered as
    /// the SCRCNI annotation in the Purpose of Change of Custody column (COC-005).
    /// </summary>
    public bool IsScrcni { get; private set; }

    public string? Destination { get; private set; }
    public string? Agency { get; private set; }

    /// <summary>
    /// The reconciliation finding this event was recorded from, when a person recorded a custody
    /// row the scan shows and the companion record lacked (REC-010). A correlation pointer, set
    /// once, outside the hash: the hashed <see cref="ItemEvent.SourceDocumentId"/> and the notes
    /// are the provenance; this only lets the reconciliation view say "recorded".
    /// </summary>
    public int? ReconciliationFindingId { get; private set; }

    public void LinkReconciliationFinding(int findingId)
    {
        if (ReconciliationFindingId is not null)
        {
            throw new AppendOnlyViolationException($"Event {Id} is already linked to reconciliation finding {ReconciliationFindingId}.");
        }

        ReconciliationFindingId = Guard.Positive(findingId, "REC-010", "Reconciliation finding");
    }

    /// <summary>The purpose text as it must appear on the form, with the SCRCNI annotation applied.</summary>
    public string PurposeForForm
        => IsScrcni ? $"{PurposeOfChangeOfCustody} ({ScrcniAnnotation})" : PurposeOfChangeOfCustody;

    /// <summary>
    /// AR 195-5 2-3f - the Change of Custody columns a correction may target.
    /// </summary>
    public override IReadOnlyDictionary<string, string?> CorrectableFields => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [nameof(ReleasedBy)] = ReleasedBy?.DisplayName,
        [nameof(ReceivedBy)] = ReceivedBy?.DisplayName,
        [nameof(PurposeOfChangeOfCustody)] = PurposeOfChangeOfCustody,
        [nameof(Destination)] = Destination,
        [nameof(Agency)] = Agency,
        [nameof(Notes)] = Notes
    };

    /// <summary>
    /// Both custody parties are ROWS, not text. A correction to either must name the replacement
    /// party, so that "who holds this item" stays a resolvable identity rather than becoming a
    /// name someone typed (COC-004).
    /// </summary>
    public override IReadOnlyDictionary<string, EventFieldReference> ReferenceFields
        => new Dictionary<string, EventFieldReference>(StringComparer.Ordinal)
        {
            [nameof(ReleasedBy)] = new(CorrectableFieldReference.CustodyParty, ReleasedByPartyId),
            [nameof(ReceivedBy)] = new(CorrectableFieldReference.CustodyParty, ReceivedByPartyId)
        };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("ReleasedBy", ReleasedBy?.DisplayName);
        yield return new("ReceivedBy", ReceivedBy?.DisplayName);
        yield return new("Purpose", PurposeOfChangeOfCustody);
        yield return new("IsScrcni", IsScrcni ? "1" : "0");
        yield return new("Destination", Destination);
        yield return new("Agency", Agency);
    }

    public override string Summarize()
        => $"Custody: {ReleasedBy?.DisplayName} → {ReceivedBy?.DisplayName} — {PurposeForForm}";
}

/// <summary>
/// A change of physical location within the evidence room.
///
/// AR 195-5 2-4e requires only the CURRENT location, "recorded in pencil on the location block
/// of the DA Form 4137", and states that "location changes in the evidence room will be kept
/// current by erasing the previous entry and noting the new location."
///
/// EMC retains the full history anyway, as a DESIGN + CONTROL decision (LOC-002), because it
/// materially improves inventory reconstruction (3-2), discrepancy investigation (3-3a) and
/// inquiry support (3-3b).
///
/// Two things must be said wherever this is discussed (LOC-003):
///   1. EMC must NOT claim AR 195-5 requires location history. It does not.
///   2. EMC's history may legitimately diverge from the paper form, which by design shows only
///      the current location. That divergence is not evidence that the form is wrong.
///
/// A temporary release is NOT a location — it is a custody state (LOC-005).
/// </summary>
public class LocationEvent : ItemEvent
{
    private LocationEvent() { }

    public LocationEvent(
        int storageLocationId,
        string storageLocationPath,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? reason = null,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, recordedByUserId, notes)
    {
        StorageLocationId = storageLocationId;
        StorageLocationPath = Guard.NotBlank(storageLocationPath, "LOC-001", "Storage location");
        Reason = Guard.TrimToNull(reason);
    }

    public override ItemEventKind Kind => ItemEventKind.Location;

    public int StorageLocationId { get; private set; }

    /// <summary>
    /// The location path as it read at the time of the event ("Shelf B / Bin 14"). Denormalized
    /// deliberately: an append-only history must remain readable exactly as recorded even if the
    /// storage location is later renamed or retired.
    /// </summary>
    public string StorageLocationPath { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    public override IReadOnlyDictionary<string, string?> CorrectableFields => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [nameof(StorageLocationPath)] = StorageLocationPath,
        [nameof(Reason)] = Reason,
        [nameof(Notes)] = Notes
    };

    /// <summary>
    /// The location is a ROW, not text. Correcting it must move the item to another
    /// StorageLocation, not merely restate the path: AR 195-5 3-2 inventories and 3-3a
    /// discrepancy work ask which items a given container holds, and that question is answered
    /// through the identifier.
    /// </summary>
    public override IReadOnlyDictionary<string, EventFieldReference> ReferenceFields
        => new Dictionary<string, EventFieldReference>(StringComparer.Ordinal)
        {
            [nameof(StorageLocationPath)] =
                new(CorrectableFieldReference.StorageLocation, StorageLocationId)
        };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("StorageLocationId", StorageLocationId.ToString("D", null));
        yield return new("StorageLocationPath", StorageLocationPath);
        yield return new("Reason", Reason);
    }

    public override string Summarize() => $"Location: {StorageLocationPath}";
}

/// <summary>
/// Sealing, breach or resealing of an evidence container.
///
/// AR 195-5 2-2a: the sealer writes initials or signature across the seals in several locations;
/// on breach the container is resealed with initials and "time and date of resealing across the
/// new seals".
/// AR 195-5 2-3e: any breach by the custodian is annotated on the DA Form 4137, and an MFR
/// describing the purpose of the breach is affixed to the original form "as a permanent
/// attachment".
/// AR 195-5 3-2f: a sealed container is not breached for any inventory unless directed by the
/// responsible supervisor, who prepares an MFR attached to the corresponding DA Form 4137.
/// </summary>
public class SealEvent : ItemEvent
{
    private SealEvent() { }

    public SealEvent(
        SealAction action,
        string performedByName,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? purposeOfBreach = null,
        string? mfrReference = null,
        string? directingSupervisorName = null,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, recordedByUserId, notes)
    {
        Action = action;
        PerformedByName = Guard.NotBlank(performedByName, "ITEM-011", "Person sealing or breaching");
        PurposeOfBreach = Guard.TrimToNull(purposeOfBreach);
        MfrReference = Guard.TrimToNull(mfrReference);
        DirectingSupervisorName = Guard.TrimToNull(directingSupervisorName);

        // AR 195-5 2-3e — a breach requires a stated purpose and an MFR affixed to the original
        // DA Form 4137 as a permanent attachment.
        if (action == SealAction.Breached)
        {
            if (PurposeOfBreach is null)
            {
                throw new DomainRuleViolationException(
                    "DOC-007",
                    "AR 195-5 2-3e: a breach of a sealed evidence container requires the purpose of "
                    + "the breach to be recorded.");
            }

            if (MfrReference is null)
            {
                throw new DomainRuleViolationException(
                    "DOC-007",
                    "AR 195-5 2-3e: an MFR describing the purpose of the breach must be prepared and "
                    + "affixed to the original DA Form 4137 as a permanent attachment.");
            }
        }
    }

    public override ItemEventKind Kind => ItemEventKind.Seal;

    public SealAction Action { get; private set; }

    /// <summary>AR 195-5 2-2a — the individual who wrote their initials across the seals.</summary>
    public string PerformedByName { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-3e.</summary>
    public string? PurposeOfBreach { get; private set; }

    /// <summary>AR 195-5 2-3e, 3-2f — the MFR attached to the original DA Form 4137.</summary>
    public string? MfrReference { get; private set; }

    /// <summary>AR 195-5 3-2f — the supervisor who directed a breach during an inventory.</summary>
    public string? DirectingSupervisorName { get; private set; }

    public override IReadOnlyDictionary<string, string?> CorrectableFields => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [nameof(PerformedByName)] = PerformedByName,
        [nameof(PurposeOfBreach)] = PurposeOfBreach,
        [nameof(MfrReference)] = MfrReference,
        [nameof(DirectingSupervisorName)] = DirectingSupervisorName,
        [nameof(Notes)] = Notes
    };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("Action", Action.ToString());
        yield return new("PerformedByName", PerformedByName);
        yield return new("PurposeOfBreach", PurposeOfBreach);
        yield return new("MfrReference", MfrReference);
        yield return new("DirectingSupervisorName", DirectingSupervisorName);
    }

    public override string Summarize()
        => $"Seal: {Action} by {PerformedByName}"
           + (PurposeOfBreach is null ? string.Empty : $" — {PurposeOfBreach}");
}

/// <summary>
/// A laboratory or forensic examination.
///
/// AR 195-5 2-7c governs submission to USACIL; 2-3j governs partial extraction of an item for
/// examination by a laboratory other than USACIL, which requires annotation on the original
/// DA Form 4137 describing what was extracted and from which item.
/// </summary>
public class ExaminationEvent : ItemEvent
{
    private ExaminationEvent() { }

    public ExaminationEvent(
        string laboratory,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? examinationRequestReference = null,
        string? exhibitNumber = null,
        string? extractionDescription = null,
        string? resultReference = null,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, recordedByUserId, notes)
    {
        Laboratory = Guard.NotBlank(laboratory, "COC-003", "Examining laboratory");
        ExaminationRequestReference = Guard.TrimToNull(examinationRequestReference);
        ExhibitNumber = Guard.TrimToNull(exhibitNumber);
        ExtractionDescription = Guard.TrimToNull(extractionDescription);
        ResultReference = Guard.TrimToNull(resultReference);
    }

    public override ItemEventKind Kind => ItemEventKind.Examination;

    public string Laboratory { get; private set; } = string.Empty;

    /// <summary>DD Form 2922 (Forensic Laboratory Examination Request) reference.</summary>
    public string? ExaminationRequestReference { get; private set; }

    /// <summary>AR 195-5 2-3j — the corresponding USACIL exhibit number.</summary>
    public string? ExhibitNumber { get; private set; }

    /// <summary>AR 195-5 2-3j — what was extracted, and from which item.</summary>
    public string? ExtractionDescription { get; private set; }

    public string? ResultReference { get; private set; }

    public override IReadOnlyDictionary<string, string?> CorrectableFields => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [nameof(Laboratory)] = Laboratory,
        [nameof(ExaminationRequestReference)] = ExaminationRequestReference,
        [nameof(ExhibitNumber)] = ExhibitNumber,
        [nameof(ExtractionDescription)] = ExtractionDescription,
        [nameof(ResultReference)] = ResultReference,
        [nameof(Notes)] = Notes
    };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("Laboratory", Laboratory);
        yield return new("ExaminationRequestReference", ExaminationRequestReference);
        yield return new("ExhibitNumber", ExhibitNumber);
        yield return new("ExtractionDescription", ExtractionDescription);
        yield return new("ResultReference", ResultReference);
    }

    public override string Summarize() => $"Examination: {Laboratory}";
}

/// <summary>
/// A workflow state transition. DESIGN — AR 195-5 has no explicit state machine, but recording
/// each transition with its actor and reason is what makes the workflow itself auditable
/// (invariant I-22).
/// </summary>
public class StatusEvent : ItemEvent
{
    private StatusEvent() { }

    public StatusEvent(
        AccountabilityStatus fromStatus,
        AccountabilityStatus toStatus,
        string reason,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, recordedByUserId, notes)
    {
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = Guard.NotBlank(reason, "IAM-001", "Reason for the status change");
    }

    public override ItemEventKind Kind => ItemEventKind.Status;

    public AccountabilityStatus FromStatus { get; private set; }
    public AccountabilityStatus ToStatus { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// Only the narrative is correctable. The states themselves are the workflow's own record -
    /// a transition that did not happen cannot be corrected into one that did.
    /// </summary>
    public override IReadOnlyDictionary<string, string?> CorrectableFields => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [nameof(Reason)] = Reason,
        [nameof(Notes)] = Notes
    };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("FromStatus", FromStatus.ToString());
        yield return new("ToStatus", ToStatus.ToString());
        yield return new("Reason", Reason);
    }

    public override string Summarize() => $"Status: {FromStatus} → {ToStatus} — {Reason}";
}

/// <summary>
/// Item-visible record that the voucher's official document number was assigned or superseded
/// (AR 195-5 2-4c, 2-7g). Recorded per item so the item's own chronological history is complete
/// without having to join to the voucher.
/// </summary>
public class DocumentNumberEvent : ItemEvent
{
    private DocumentNumberEvent() { }

    public DocumentNumberEvent(
        string documentNumber,
        string? previousDocumentNumber,
        bool attestedAssignedInAuthoritativeLedger,
        DateTimeOffset occurredAtLocal,
        DateTimeOffset recordedAtUtc,
        int recordedByUserId,
        string? notes = null)
        : base(occurredAtLocal, recordedAtUtc, recordedByUserId, notes)
    {
        DocumentNumber = Guard.NotBlank(documentNumber, "VCH-004", "Evidence document number");
        PreviousDocumentNumber = Guard.TrimToNull(previousDocumentNumber);
        AttestedAssignedInAuthoritativeLedger = attestedAssignedInAuthoritativeLedger;
    }

    public override ItemEventKind Kind => ItemEventKind.DocumentNumber;

    public string DocumentNumber { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-7g — the prior number, which remains legible.</summary>
    public string? PreviousDocumentNumber { get; private set; }

    public bool AttestedAssignedInAuthoritativeLedger { get; private set; }

    /// <summary>
    /// The document number itself is NOT correctable here. AR 195-5 2-4c makes assignment an act
    /// performed in the authoritative ledger, and 2-7g supersedes a number with a new assignment
    /// rather than editing the old one - so a change goes through
    /// OfficialDocumentNumberAssignment, not through a correction to this event.
    /// </summary>
    public override IReadOnlyDictionary<string, string?> CorrectableFields => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [nameof(Notes)] = Notes
    };

    public override IEnumerable<KeyValuePair<string, string?>> CanonicalFields()
    {
        foreach (var field in base.CanonicalFields())
        {
            yield return field;
        }

        yield return new("DocumentNumber", DocumentNumber);
        yield return new("PreviousDocumentNumber", PreviousDocumentNumber);
        yield return new(
            "AttestedAssignedInAuthoritativeLedger",
            AttestedAssignedInAuthoritativeLedger ? "1" : "0");
    }

    public override string Summarize()
        => PreviousDocumentNumber is null
            ? $"Evidence document number assigned: {DocumentNumber}"
            : $"Evidence document number {PreviousDocumentNumber} superseded by {DocumentNumber}";
}
