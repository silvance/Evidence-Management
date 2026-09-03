using Emc.Domain.Common;

namespace Emc.Domain.Reconciliation;

/// <summary>What kind of difference between the verified scan and the companion record a finding concerns.</summary>
public enum ReconciliationDifferenceKind
{
    HeaderField = 1,

    /// <summary>The document number the scan shows against the one recorded. Never applied by software (REC-005).</summary>
    DocumentNumber = 2,
    ItemField = 3,

    /// <summary>An item row on the scan with no line on the companion record.</summary>
    MissingItem = 4,

    /// <summary>A line on the companion record with no item row on the scan.</summary>
    ExtraItem = 5,

    /// <summary>A chain-of-custody row on the scan; the companion record's chain is compared where it exists.</summary>
    CustodyRow = 6,
    Disposition = 7
}

/// <summary>
/// The decisions a person can take about one difference. The first is the only one that changes
/// anything, and it changes a DRAFT: a form still with the agent, or one the custodian returned
/// under 2-3g, whose next submission is a new revision (VCH-025). Everything after acceptance is
/// a finding; an error in the accepted record is corrected through para 1-7c(3), never here.
/// </summary>
public enum ReconciliationDecision
{
    /// <summary>Pre-acceptance only. The companion draft was changed to say what the verified scan says.</summary>
    AppliedToDraftForm = 1,

    /// <summary>The verified value is wrong (a misread the verifier missed, or a mis-verification); the companion record stands.</summary>
    ExtractionIncorrect = 2,

    /// <summary>A representation difference only (spacing, case, abbreviation); the companion record is right as it is.</summary>
    CompanionRecordAlreadyCorrect = 3,

    /// <summary>A person with custodial authority must look at this.</summary>
    FlagForCustodianReview = 4,

    /// <summary>The ACCEPTED record is wrong. Opens the AR 195-5 para 1-7c(3) path: supervisor informed, MFR, correction event with this scan as provenance.</summary>
    InitiatePostAcceptanceCorrection = 5,

    /// <summary>The scan shows an event (a custody transfer, a disposition) the companion record lacks. Recorded for the workflow that owns that event.</summary>
    RecordMissingHistoricalEvent = 6
}

/// <summary>
/// One person's decision about one difference between a verified scan and the companion record.
/// Append-only: a later decision on the same difference is a later row. The values it records
/// are what was compared at the time, so the finding stays meaningful after either side changes.
/// </summary>
public sealed class ReconciliationFinding : Entity, IAppendOnly
{
    public const int MaxValueLength = 4000;

    private ReconciliationFinding() { }

    public ReconciliationFinding(
        int ocrRunId, int sourceDocumentId, int voucherId, int? evidenceItemId,
        ReconciliationDifferenceKind kind, string fieldKey, string? companionValue, string? documentValue,
        ReconciliationDecision decision, string? narrative, int decidedByUserId, DateTimeOffset decidedAtUtc)
    {
        OcrRunId = Guard.Positive(ocrRunId, "REC-004", "Run");
        SourceDocumentId = Guard.Positive(sourceDocumentId, "REC-004", "Source document");
        VoucherId = Guard.Positive(voucherId, "REC-004", "Voucher");
        EvidenceItemId = evidenceItemId;
        Kind = kind;
        FieldKey = Guard.NotBlank(fieldKey, "REC-004", "Field key");
        CompanionValue = Truncate(Guard.TrimToNull(companionValue));
        DocumentValue = Truncate(Guard.TrimToNull(documentValue));
        Decision = decision;
        Narrative = Guard.TrimToNull(narrative);
        DecidedByUserId = Guard.Positive(decidedByUserId, "REC-004", "Deciding user");
        DecidedAtUtc = AccountabilityTime.Normalize(decidedAtUtc);

        if (decision is ReconciliationDecision.InitiatePostAcceptanceCorrection or ReconciliationDecision.RecordMissingHistoricalEvent
                        or ReconciliationDecision.FlagForCustodianReview or ReconciliationDecision.ExtractionIncorrect
            && Narrative is null)
        {
            throw new DomainRuleViolationException("REC-004", $"A {decision} decision states why, in a narrative.");
        }

        if (kind == ReconciliationDifferenceKind.DocumentNumber && decision == ReconciliationDecision.AppliedToDraftForm)
        {
            throw new DomainRuleViolationException("REC-005",
                "A document number is never applied from a scan. AR 195-5 2-4c: the custodian assigns it in the ledger; EMC transcribes it, with the custodian's attestation, on the voucher page.");
        }

        if (Narrative?.Length > MaxValueLength)
        {
            throw new DomainRuleViolationException("REC-004", $"Narrative exceeds {MaxValueLength} characters.");
        }
    }

    public int OcrRunId { get; private set; }
    public int SourceDocumentId { get; private set; }
    public int VoucherId { get; private set; }
    public int? EvidenceItemId { get; private set; }
    public ReconciliationDifferenceKind Kind { get; private set; }
    public string FieldKey { get; private set; } = string.Empty;

    /// <summary>What the companion record said when the decision was taken.</summary>
    public string? CompanionValue { get; private set; }

    /// <summary>What the verified scan said when the decision was taken.</summary>
    public string? DocumentValue { get; private set; }
    public ReconciliationDecision Decision { get; private set; }
    public string? Narrative { get; private set; }
    public int DecidedByUserId { get; private set; }
    public DateTimeOffset DecidedAtUtc { get; private set; }

    private static string? Truncate(string? value) => value is { Length: > MaxValueLength } ? value[..MaxValueLength] : value;
}
