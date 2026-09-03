namespace Emc.Domain.Identity;

/// <summary>
/// Role names. Authorization is resolved server-side, per request, from the database.
/// Client-submitted role information is never trusted (IAM-002).
/// </summary>
public static class EmcRoles
{
    /// <summary>AR 195-5 2-3b — the agent who first acquires evidence prepares the DA Form 4137.</summary>
    public const string Agent = "Agent";

    /// <summary>AR 195-5 1-4g(1), 1-4h, 1-7a(1)(c).</summary>
    public const string PrimaryEvidenceCustodian = "PrimaryEvidenceCustodian";

    /// <summary>AR 195-5 1-4i — acts during the primary's temporary absence.</summary>
    public const string AlternateEvidenceCustodian = "AlternateEvidenceCustodian";

    /// <summary>AR 195-5 1-4g(3), 3-1b(2), 2-8c.</summary>
    public const string CommanderOrSac = "CommanderOrSac";

    /// <summary>AR 195-5 3-1, 3-2 — restricted to an assigned inspection/inventory session.</summary>
    public const string InspectorOrInventoryParticipant = "InspectorOrInventoryParticipant";

    /// <summary>
    /// Administers accounts, roles, storage locations and configuration.
    ///
    /// This role grants NO evidence-accountability permission (IAM-009, invariant I-13). It is
    /// not a superset of the other roles, and there is no administrator bypass anywhere in the
    /// codebase. An administrator can of course grant themselves a custodian role — no
    /// application can prevent that — so EMC makes it loud instead: role grants are audited and
    /// self-grants are flagged (IAM-010).
    /// </summary>
    public const string ApplicationAdministrator = "ApplicationAdministrator";

    public static readonly IReadOnlyList<string> All =
    [
        Agent,
        PrimaryEvidenceCustodian,
        AlternateEvidenceCustodian,
        CommanderOrSac,
        InspectorOrInventoryParticipant,
        ApplicationAdministrator
    ];

    /// <summary>
    /// Roles that may hold custodial authority — but only while an active
    /// <see cref="Emc.Domain.Identity.CustodianAppointment"/> exists (IAM-005, invariant I-11).
    /// The role alone is never sufficient.
    /// </summary>
    public static readonly IReadOnlyList<string> CustodianRoles =
    [
        PrimaryEvidenceCustodian,
        AlternateEvidenceCustodian
    ];
}
