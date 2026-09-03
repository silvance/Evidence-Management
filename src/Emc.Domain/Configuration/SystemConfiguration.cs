using Emc.Domain.Common;

namespace Emc.Domain.Configuration;

/// <summary>
/// System-wide configuration.
///
/// Two settings here carry regulatory weight and must not be changed casually:
/// <see cref="AuthoritativeMode"/> and <see cref="NumberingMode"/>. Together they decide whether
/// EMC is operating as an AR 195-5 2-5c companion ("used in conjunction with or to enhance the
/// requirements of this regulation" — no approval needed) or as a stand-alone automated evidence
/// ledger/accountability system, which for CI organizations requires prior Army G-2X approval.
/// </summary>
public class SystemConfiguration : Entity, IConcurrencyStamped
{
    private SystemConfiguration() { }

    public SystemConfiguration(string organizationName, string accreditedClassificationLevel)
    {
        OrganizationName = Guard.NotBlank(organizationName, "EMC-003", "Organization name");
        AccreditedClassificationLevel = Guard.NotBlank(
            accreditedClassificationLevel, "SEC-003", "Accredited classification level");
        AuthoritativeMode = AuthoritativeMode.Companion;
        NumberingMode = NumberingMode.ManualTranscription;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public string OrganizationName { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-5c. Defaults to <see cref="AuthoritativeMode.Companion"/>.</summary>
    public AuthoritativeMode AuthoritativeMode { get; private set; }

    /// <summary>AR 195-5 2-4c. Defaults to <see cref="NumberingMode.ManualTranscription"/>.</summary>
    public NumberingMode NumberingMode { get; private set; }

    /// <summary>
    /// The G-2X approval reference, recorded if and when approval is obtained (AR 195-5 2-5c).
    /// Required before <see cref="AuthoritativeMode.AuthoritativeLedger"/> may be selected.
    /// </summary>
    public string? AutomatedSystemApprovalReference { get; private set; }

    public DateTimeOffset? AutomatedSystemApprovalDate { get; private set; }

    /// <summary>
    /// The classification level this deployment is accredited for. Open decision DEC-06: an
    /// aggregation of CI evidence descriptions may itself be classified, which changes the
    /// accreditation, the hosting enclave and the backup handling. This must be settled before
    /// EMC holds real data. AR 195-5 delegates classified handling to AR 380-5 (2-6h, 2-7k,
    /// 2-9r, 4-1a); EMC invents no classified requirements of its own.
    /// </summary>
    public string AccreditedClassificationLevel { get; private set; } = "UNCLASSIFIED";

    /// <summary>
    /// LOCAL management threshold, in days, after which a temporary release is flagged for
    /// review. AR 195-5 gives NO numeric limit for any temporary-release category — 2-7a requires
    /// "reasonable and adequate contact" and 2-7b/3-1a(4) require that release not be for "an
    /// excessive period". EMC must never present this number as an AR 195-5 deadline (SUSP-004).
    /// </summary>
    public int LocalSuspenseReviewThresholdDays { get; private set; } = 60;

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>The notice shown on every accountability view in companion mode (EMC-003).</summary>
    public string AuthoritativeRecordNotice
        => AuthoritativeMode == AuthoritativeMode.Companion
            ? "COMPANION SYSTEM - The bound evidence ledger and the original DA Form 4137 remain "
              + "the authoritative records of accountability (AR 195-5, paras 2-5a and 2-5c). "
              + "This application assists that process; it does not replace it."
            : "AUTHORITATIVE AUTOMATED EVIDENCE LEDGER - Operating under approval reference "
              + $"{AutomatedSystemApprovalReference} (AR 195-5, para 2-5c).";

    /// <summary>
    /// AR 195-5 2-5c — a stand-alone automated evidence ledger/accountability system requires
    /// prior approval, which for CI organizations is granted by Army G-2X. EMC will not switch
    /// mode without a recorded approval reference (EMC-004, EMC-005).
    /// </summary>
    public void EnableAuthoritativeLedgerMode(string approvalReference, DateTimeOffset approvalDate)
    {
        AutomatedSystemApprovalReference =
            Guard.NotBlank(approvalReference, "EMC-005", "Approval reference");
        AutomatedSystemApprovalDate = approvalDate;
        AuthoritativeMode = AuthoritativeMode.AuthoritativeLedger;
    }

    /// <summary>
    /// AR 195-5 2-4c and 2-5c — system-assigned sequential numbering is only lawful once the
    /// system is an approved automated equivalent. Refused outright in companion mode (EMC-002).
    /// </summary>
    public void EnableSystemAssignedNumbering()
    {
        if (AuthoritativeMode != AuthoritativeMode.AuthoritativeLedger)
        {
            throw new DomainRuleViolationException(
                "EMC-002",
                "AR 195-5 2-4c and 2-5c: the evidence custodian assigns the document number by "
                + "order of precedence from the evidence ledger. This application may not assign "
                + "it until it has been approved as an automated equivalent - for CI "
                + "organizations, by Army G-2X.");
        }

        NumberingMode = NumberingMode.SystemAssigned;
    }

    public void SetLocalSuspenseReviewThreshold(int days)
    {
        if (days <= 0)
        {
            throw new DomainRuleViolationException(
                "SUSP-004", "The local suspense review threshold must be a positive number of days.");
        }

        LocalSuspenseReviewThresholdDays = days;
    }

    public void SetAccreditedClassificationLevel(string level)
        => AccreditedClassificationLevel =
            Guard.NotBlank(level, "SEC-003", "Accredited classification level");
}
