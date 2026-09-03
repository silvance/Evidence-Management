using Emc.Domain.Identity;

namespace Emc.Application.Authorization;

/// <summary>
/// Which roles hold which permissions.
///
/// The single most important entry here is <see cref="EmcRoles.ApplicationAdministrator"/>,
/// which holds ONLY administrative permissions and NO evidence-accountability permission at all
/// (IAM-009, invariant I-13). It is not a superset of the other roles, and no code path grants
/// it an accountability permission implicitly.
///
/// An administrator can of course grant themselves a custodian role — no application can prevent
/// that. What EMC does instead is make it loud: role grants are audited and self-grants are
/// flagged (IAM-010), and out-of-band tampering is detectable through the per-item hash chain
/// (AUD-008).
/// </summary>
public static class RolePermissionMap
{
    private static readonly Dictionary<string, HashSet<string>> Map = new(StringComparer.Ordinal)
    {
        // AR 195-5 2-3b — the agent who first acquires evidence prepares the DA Form 4137.
        // Explicitly NOT: assigning document numbers (2-4c is the custodian's act), accepting
        // evidence into the evidence room, or anything else reserved to the custodian (IAM-011).
        [EmcRoles.Agent] = new(StringComparer.Ordinal)
        {
            EmcPermissions.CreateCase,
            EmcPermissions.CreateDraftVoucher,
            EmcPermissions.EditDraftVoucher,
            EmcPermissions.SubmitVoucherForIntake,
            EmcPermissions.UploadSourceDocument
        },

        // AR 195-5 1-4h — the primary custodian accounts for, preserves, safeguards and (when
        // authorized) disposes of all evidence, and maintains all evidence records.
        [EmcRoles.PrimaryEvidenceCustodian] = new(StringComparer.Ordinal)
        {
            EmcPermissions.CreateCase,
            EmcPermissions.CreateDraftVoucher,
            EmcPermissions.EditDraftVoucher,
            EmcPermissions.SubmitVoucherForIntake,
            EmcPermissions.UploadSourceDocument,
            EmcPermissions.AcceptEvidenceIntake,
            EmcPermissions.RecordOfficialDocumentNumber,
            EmcPermissions.AssignStorageLocation,
            EmcPermissions.RecordCustodyEvent,
            EmcPermissions.ReleaseTemporarily,
            EmcPermissions.ReturnFromTemporaryRelease,
            EmcPermissions.PerformDisposition,
            EmcPermissions.RecordCorrection,
            EmcPermissions.ParticipateInInventory,
            EmcPermissions.ViewManagementDashboard
        },

        // AR 195-5 1-4i — the alternate assumes the primary's duties during a temporary absence.
        // Same permission set; the difference is enforced by the appointment window, not here
        // (IAM-006, DEC-05).
        [EmcRoles.AlternateEvidenceCustodian] = new(StringComparer.Ordinal)
        {
            EmcPermissions.CreateCase,
            EmcPermissions.CreateDraftVoucher,
            EmcPermissions.EditDraftVoucher,
            EmcPermissions.SubmitVoucherForIntake,
            EmcPermissions.UploadSourceDocument,
            EmcPermissions.AcceptEvidenceIntake,
            EmcPermissions.RecordOfficialDocumentNumber,
            EmcPermissions.AssignStorageLocation,
            EmcPermissions.RecordCustodyEvent,
            EmcPermissions.ReleaseTemporarily,
            EmcPermissions.ReturnFromTemporaryRelease,
            EmcPermissions.PerformDisposition,
            EmcPermissions.RecordCorrection,
            EmcPermissions.ParticipateInInventory,
            EmcPermissions.ViewManagementDashboard
        },

        // AR 195-5 1-4g(3), 3-1b(2), 2-8c — inspections, attestations, discrepancy review,
        // approvals, dashboards.
        [EmcRoles.CommanderOrSac] = new(StringComparer.Ordinal)
        {
            EmcPermissions.ConductInspection,
            EmcPermissions.ReviewDiscrepancy,
            EmcPermissions.ApproveDisposition,
            EmcPermissions.ParticipateInInventory,
            EmcPermissions.ViewManagementDashboard
        },

        // AR 195-5 3-1, 3-2 — restricted to an assigned inspection or inventory session.
        [EmcRoles.InspectorOrInventoryParticipant] = new(StringComparer.Ordinal)
        {
            EmcPermissions.ParticipateInInventory
        },

        // Administration only. Deliberately contains no accountability permission (IAM-009).
        [EmcRoles.ApplicationAdministrator] = new(StringComparer.Ordinal)
        {
            EmcPermissions.ManageUsers,
            EmcPermissions.ManageRoles,
            EmcPermissions.ManageStorageLocations,
            EmcPermissions.ManageSystemConfiguration,
            EmcPermissions.VerifyIntegrity
        }
    };

    public static bool RoleHasPermission(string role, string permission)
        => Map.TryGetValue(role, out var permissions) && permissions.Contains(permission);

    public static bool AnyRoleHasPermission(IEnumerable<string> roles, string permission)
        => roles.Any(r => RoleHasPermission(r, permission));

    public static IReadOnlySet<string> PermissionsFor(string role)
        => Map.TryGetValue(role, out var permissions) ? permissions : new HashSet<string>();
}
