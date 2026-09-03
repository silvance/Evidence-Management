namespace Emc.Application.Authorization;

/// <summary>
/// Named permissions. Every accountability operation requires one; there is no default-allow
/// and no administrator bypass branch anywhere in the codebase (IAM-009, invariant I-13).
/// </summary>
public static class EmcPermissions
{
    // --- Read surface ---
    //
    // Reads are permissions in their own right. Authentication is NOT authorization: a domain
    // user who authenticates successfully but holds no EMC role must see no case control
    // numbers, evidence descriptions, serial numbers, custody history or locations (IAM-017).
    public const string ViewCase = "case.view";
    public const string ViewVoucher = "voucher.view";
    public const string ViewEvidenceItem = "evidence-item.view";
    public const string ViewEvidenceHistory = "evidence-history.view";
    public const string ViewSourceDocument = "source-document.view";
    public const string DownloadSourceDocument = "source-document.download";
    public const string ViewAudit = "audit.view";
    public const string ViewConfiguration = "configuration.view";

    // --- Agent surface (AR 195-5 2-3b) ---
    public const string CreateCase = "case.create";
    public const string CreateDraftVoucher = "voucher.create";
    public const string EditDraftVoucher = "voucher.edit-draft";
    public const string SubmitVoucherForIntake = "voucher.submit";
    public const string UploadSourceDocument = "document.upload";

    // --- Evidence room surface (AR 195-5 1-4h, 2-4c, 2-4e, 2-7) ---
    public const string AcceptEvidenceIntake = "evidence.accept";
    public const string RecordOfficialDocumentNumber = "evidence.record-document-number";
    public const string AssignStorageLocation = "evidence.assign-location";
    public const string RecordCustodyEvent = "evidence.record-custody";
    public const string ReleaseTemporarily = "evidence.release-temporary";
    public const string ReturnFromTemporaryRelease = "evidence.return";
    public const string PerformDisposition = "evidence.dispose";
    public const string RecordCorrection = "evidence.record-correction";

    // --- Supervisory surface (AR 195-5 1-4g(3), 3-1, 3-2, 2-8) ---
    public const string ConductInspection = "inspection.conduct";
    public const string ReviewDiscrepancy = "discrepancy.review";
    public const string ApproveDisposition = "disposition.approve";
    public const string ViewManagementDashboard = "dashboard.view";

    // --- Inventory surface (AR 195-5 3-1b(2), 3-2) ---
    public const string ParticipateInInventory = "inventory.participate";

    // --- Administration. Note what is NOT here: no accountability permission (IAM-009). ---
    public const string ManageUsers = "admin.users";
    public const string ManageRoles = "admin.roles";
    public const string ManageStorageLocations = "admin.storage-locations";
    public const string ManageSystemConfiguration = "admin.configuration";
    public const string VerifyIntegrity = "admin.verify-integrity";

    /// <summary>
    /// Permissions that administer the application rather than an evidence room, and are
    /// therefore held globally. Everything not in this set requires an evidence room.
    /// </summary>
    public static readonly IReadOnlySet<string> GlobalPermissions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ManageUsers,
            ManageRoles,
            ManageStorageLocations,
            ManageSystemConfiguration,
            VerifyIntegrity,
            ViewConfiguration
        };

    /// <summary>
    /// Read permissions over evidence content. Grouped so the administrator-denial test can
    /// assert over all of them, and so no read can be added without landing in that test.
    /// </summary>
    public static readonly IReadOnlySet<string> EvidenceReadPermissions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ViewCase,
            ViewVoucher,
            ViewEvidenceItem,
            ViewEvidenceHistory,
            ViewSourceDocument,
            DownloadSourceDocument,
            ViewAudit
        };

    /// <summary>
    /// Permissions that additionally require an ACTIVE CustodianAppointment, not merely a
    /// custodian role (IAM-005, invariant I-11).
    ///
    /// AR 195-5 vests custodial authority in a person named in a written appointment
    /// (1-4g(1), 1-7b), and the alternate holds it only during the primary's temporary absence
    /// (1-4i). A role flag alone is never sufficient.
    /// </summary>
    public static readonly IReadOnlySet<string> RequireActiveCustodianAppointment =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AcceptEvidenceIntake,
            RecordOfficialDocumentNumber,
            AssignStorageLocation,
            RecordCustodyEvent,
            ReleaseTemporarily,
            ReturnFromTemporaryRelease,
            PerformDisposition
        };

    /// <summary>
    /// Accountability permissions. An ApplicationAdministrator is denied every one of these
    /// (IAM-009). A test asserts this set is exactly the set of permissions the administrator
    /// cannot hold, so adding a permission here without granting it to a real role fails loudly
    /// rather than silently creating an unreachable operation.
    /// </summary>
    public static readonly IReadOnlySet<string> AccountabilityPermissions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ViewCase,
            ViewVoucher,
            ViewEvidenceItem,
            ViewEvidenceHistory,
            ViewSourceDocument,
            DownloadSourceDocument,
            ViewAudit,
            CreateCase,
            CreateDraftVoucher,
            EditDraftVoucher,
            SubmitVoucherForIntake,
            UploadSourceDocument,
            AcceptEvidenceIntake,
            RecordOfficialDocumentNumber,
            AssignStorageLocation,
            RecordCustodyEvent,
            ReleaseTemporarily,
            ReturnFromTemporaryRelease,
            PerformDisposition,
            RecordCorrection,
            ConductInspection,
            ReviewDiscrepancy,
            ApproveDisposition,
            ParticipateInInventory
        };
}
