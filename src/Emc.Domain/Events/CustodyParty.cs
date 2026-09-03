using Emc.Domain.Common;
using Emc.Domain.Identity;

namespace Emc.Domain.Events;

/// <summary>
/// A party to a change of custody.
///
/// This exists because a chain-of-custody counterparty is frequently NOT an EMC user, so custody
/// fields cannot be a foreign key to User. AR 195-5 contemplates counterparties that are:
///
///   - an internal user (agent, custodian);
///   - an external person — trial counsel, a civilian prosecutor, an Art. 32 investigating
///     officer, a property owner (2-7b, 2-8e(4));
///   - an organization — USACIL, AFMES/DFT, the US Secret Service, another agency (2-7c, 2-9d);
///   - a REGISTERED OR OTHER ACCOUNTABLE MAIL NUMBER — 2-7e: "The evidence custodian will only
///     enter the registered or other accountable mail number in the Received by block of the
///     chain of custody section of the DA Form 4137";
///   - the literal "N/A Custodian Unable to Sign" — 3-2g(5).
///
/// Forcing a User foreign key here would have made the regulation's own examples
/// unrepresentable (COC-004, COC-006, COC-007).
/// </summary>
public class CustodyParty : Entity
{
    /// <summary>AR 195-5 3-2g(5).</summary>
    public const string CustodianUnableToSignText = "N/A Custodian Unable to Sign";

    private CustodyParty() { }

    private CustodyParty(CustodyPartyKind kind, string displayName)
    {
        Kind = kind;
        DisplayName = displayName;
    }

    public CustodyPartyKind Kind { get; private set; }

    /// <summary>Name, organization, mail number or prescribed text, as it appears on the form.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    public int? UserId { get; private set; }
    public User? User { get; private set; }

    /// <summary>Grade, title or position — trial counsel, SA, GS-12 (AR 195-5 2-7b).</summary>
    public string? TitleOrGrade { get; private set; }

    public string? OrganizationOrAgency { get; private set; }

    /// <summary>AR 195-5 2-7e — the registered or other accountable mail number.</summary>
    public string? AccountableMailNumber { get; private set; }

    /// <summary>
    /// AR 195-5 2-7b — "A person receiving evidence, either on a temporary or on a permanent
    /// basis, will present appropriate identification." Records that this occurred.
    /// </summary>
    public bool IdentificationVerified { get; private set; }

    public static CustodyParty ForUser(User user, bool identificationVerified = true)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new CustodyParty(CustodyPartyKind.InternalUser, user.PrintedNameAndGrade)
        {
            UserId = user.Id,
            TitleOrGrade = user.RankOrGrade,
            OrganizationOrAgency = user.OrganizationOrUnit,
            IdentificationVerified = identificationVerified
        };
    }

    public static CustodyParty ForExternalPerson(
        string name,
        string? titleOrGrade,
        string? organizationOrAgency,
        bool identificationVerified)
    {
        // AR 195-5 2-7b requires the recipient to present appropriate identification. EMC records
        // whether that happened rather than silently assuming it did.
        return new CustodyParty(
            CustodyPartyKind.ExternalPerson,
            Guard.NotBlank(name, "COC-004", "Party name"))
        {
            TitleOrGrade = Guard.TrimToNull(titleOrGrade),
            OrganizationOrAgency = Guard.TrimToNull(organizationOrAgency),
            IdentificationVerified = identificationVerified
        };
    }

    public static CustodyParty ForOrganization(string organizationName)
        => new(CustodyPartyKind.Organization,
            Guard.NotBlank(organizationName, "COC-004", "Organization name"))
        {
            OrganizationOrAgency = organizationName.Trim()
        };

    /// <summary>AR 195-5 2-7e — a registered or other accountable mail number as the custody party.</summary>
    public static CustodyParty ForAccountableMailNumber(string mailNumber, string? carrier = null)
    {
        var number = Guard.NotBlank(mailNumber, "COC-006", "Accountable mail number");

        return new CustodyParty(CustodyPartyKind.AccountableMailNumber, number)
        {
            AccountableMailNumber = number,
            OrganizationOrAgency = Guard.TrimToNull(carrier)
        };
    }

    /// <summary>AR 195-5 3-2g(5) — the primary custodian is unable to sign the Released By block.</summary>
    public static CustodyParty CustodianUnableToSign()
        => new(CustodyPartyKind.CustodianUnableToSign, CustodianUnableToSignText);
}
