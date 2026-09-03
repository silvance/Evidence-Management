namespace Emc.Domain.Common;

/// <summary>
/// Workflow state of an evidence item. Deliberately separate from custody state, physical
/// location and disposition state — collapsing those axes into one status loses information
/// (docs/domain-model.md §3).
/// </summary>
public enum AccountabilityStatus
{
    /// <summary>Voucher is being prepared. Items may still be added, edited and removed.</summary>
    Draft = 0,

    /// <summary>AR 195-5 2-1a / 2-3b — the agent has custody and is preparing the DA Form 4137.</summary>
    Acquired = 1,

    /// <summary>AR 195-5 4-3a — secured in a temporary evidence facility during non-duty hours.</summary>
    TemporaryStorage = 2,

    /// <summary>AR 195-5 2-4a — submitted; due to the custodian NLT the first working day after acquisition.</summary>
    AwaitingCustodian = 3,

    /// <summary>AR 195-5 2-4c — accepted by the custodian; the voucher carries an official document number.</summary>
    InEvidenceRoom = 4,

    /// <summary>AR 195-5 2-7a — out of the evidence room on authorized temporary release.</summary>
    TemporarilyReleased = 5,

    /// <summary>AR 195-5 2-8 — disposition requested; awaiting authority and action.</summary>
    DispositionPending = 6,

    /// <summary>AR 195-5 2-9 — terminal. Final disposal action complete.</summary>
    Disposed = 7,

    /// <summary>AR 195-5 3-3a — cannot be located; within the 5-working-day resolution period.</summary>
    DiscrepancyReview = 8,

    /// <summary>AR 195-5 3-3b — an official inquiry has been initiated under AR 15-6.</summary>
    Inquiry = 9,

    /// <summary>
    /// AR 195-5 3-3c — terminal. Relief for accountability granted (for CI units, by Army G-2X),
    /// which permits closure of the DA Form 4137. Distinct from <see cref="Disposed"/>: the item
    /// was never disposed of; accountability for it was relieved. Merging the two would misstate
    /// the record.
    /// </summary>
    ReliefGranted = 10,

    /// <summary>AR 195-5 2-13 — sealed into a long-term retention container. Voucher stays active (2-13b).</summary>
    LongTermRetention = 11,

    /// <summary>
    /// AR 195-5 2-7g — terminal for this evidence room. Permanently transferred to another
    /// evidence room, which assigns its own next document number. Not disposition.
    /// </summary>
    PermanentlyTransferred = 12
}

/// <summary>Derived from contained items — never stored as maintained state (AR 195-5 2-4h).</summary>
public enum VoucherDerivedStatus
{
    Draft = 0,
    AwaitingCustodianAcceptance = 1,
    PartiallyAccepted = 2,
    Active = 3,

    /// <summary>AR 195-5 2-4h — all items disposed. Only then does the voucher become inactive.</summary>
    Inactive = 4
}

public enum ItemEventKind
{
    Custody = 1,
    Location = 2,
    Seal = 3,
    Examination = 4,
    Status = 5,
    DocumentNumber = 6,
    Correction = 7
}

/// <summary>
/// AR 195-5 2-7b and 2-7e. A chain-of-custody counterparty is frequently NOT an application
/// user, so custody parties cannot be a foreign key to User: 2-7e directs that the registered
/// or other accountable mail number be entered in the Received By block, and 3-2g(5) requires
/// the literal "N/A Custodian Unable to Sign" in the Released By block.
/// </summary>
public enum CustodyPartyKind
{
    /// <summary>An authenticated EMC user (agent, custodian).</summary>
    InternalUser = 1,

    /// <summary>Trial counsel, civilian prosecutor, Art. 32 investigating officer, property owner.</summary>
    ExternalPerson = 2,

    /// <summary>USACIL, AFMES/DFT, US Secret Service, another law enforcement agency.</summary>
    Organization = 3,

    /// <summary>AR 195-5 2-7e — a registered or other accountable mail number.</summary>
    AccountableMailNumber = 4,

    /// <summary>AR 195-5 3-2g(5) — "N/A Custodian Unable to Sign".</summary>
    CustodianUnableToSign = 5
}

public enum StorageLocationKind
{
    /// <summary>AR 195-5 4-1 — a structure, room or vault meeting the chapter 4 standards.</summary>
    EvidenceRoom = 1,

    /// <summary>AR 195-5 4-1d — a GSA-approved safe in a locked, controlled-access room.</summary>
    EvidenceDepository = 2,

    /// <summary>AR 195-5 4-3 — safe/filing cabinet, CONEX, building, room or fenced enclosure.</summary>
    TemporaryEvidenceFacility = 3,

    /// <summary>AR 195-5 2-6f — impoundment lot, warehouse or other reasonably secure place.</summary>
    ImpoundLotOrWarehouse = 4,

    /// <summary>AR 195-5 2-13 — a sealed long-term retention box or crate.</summary>
    LongTermStorageContainer = 5,

    /// <summary>Safe or container for firearms, high value items and drugs (App B-4b(9)).</summary>
    HighValueContainer = 6,

    Shelf = 7,
    Bin = 8
}

public enum CustodianAppointmentType
{
    /// <summary>AR 195-5 1-4g(1), 1-4h.</summary>
    Primary = 1,

    /// <summary>AR 195-5 1-4g(1), 1-4i.</summary>
    Alternate = 2
}

public enum SealAction
{
    Sealed = 1,
    Breached = 2,
    Resealed = 3
}

/// <summary>
/// AR 195-5 2-5c. Selects whether EMC is a companion or (post-approval) the authoritative
/// automated equivalent. For CI organizations the second requires Army G-2X approval.
/// </summary>
public enum AuthoritativeMode
{
    /// <summary>V1. The bound ledger and the original DA Form 4137 remain authoritative.</summary>
    Companion = 0,

    /// <summary>Requires prior Army G-2X approval for CI organizations (AR 195-5 2-5c).</summary>
    AuthoritativeLedger = 1
}

/// <summary>AR 195-5 2-4c. Who assigns the sequential evidence document number.</summary>
public enum NumberingMode
{
    /// <summary>V1. The custodian assigns it in the bound ledger; EMC transcribes it.</summary>
    ManualTranscription = 0,

    /// <summary>Requires <see cref="AuthoritativeMode.AuthoritativeLedger"/> and G-2X approval.</summary>
    SystemAssigned = 1
}

/// <summary>Security/administrative audit categories — distinct from the accountability record.</summary>
public enum AuditEventType
{
    SignIn = 1,
    SignInDenied = 2,
    PermissionDenied = 3,
    RoleGranted = 4,
    RoleRevoked = 5,
    ConfigurationChanged = 6,
    SourceDocumentDownloaded = 7,
    DataExported = 8,
    IntegrityVerificationRun = 9,
    CustodianAppointmentRecorded = 10,
    AccountabilityActionRecorded = 11
}
