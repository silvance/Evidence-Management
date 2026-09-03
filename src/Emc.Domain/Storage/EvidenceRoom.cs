using Emc.Domain.Common;

namespace Emc.Domain.Storage;

/// <summary>
/// An evidence room, depository or facility — the accountability boundary.
///
/// AR 195-5 4-1 defines an evidence room as a structure, room or vault meeting the chapter 4
/// standards; 4-1d permits a depository (a GSA-approved safe in a locked, controlled-access
/// room) where evidence volume does not justify a room.
///
/// This is the scope of nearly everything that matters:
///   2-4c  the document-number series ("001 for the first DA Form 4137 received for the
///         calendar year") runs per room per calendar year;
///   2-7g  on permanent transfer the receiving room assigns "the next document number of the
///         receiving evidence room";
///   1-4g(1) custodian appointments are per room;
///   3-1, 3-2 inspections and inventories are per room.
///
/// Whether one deployment serves one room or several is open decision DEC-03. The key is
/// present from day one either way, because retrofitting it into an accountability schema that
/// already holds real data is expensive and risky.
/// </summary>
public class EvidenceRoom : Entity, IConcurrencyStamped
{
    private EvidenceRoom() { }

    public EvidenceRoom(string name, string organizationOrUnit, string timeZoneId)
    {
        Name = Guard.NotBlank(name, "VCH-005", "Evidence room name");
        OrganizationOrUnit = Guard.NotBlank(organizationOrUnit, "VCH-005", "Organization or unit");
        TimeZoneId = Guard.NotBlank(timeZoneId, "AUD-011", "Time zone");
        IsActive = true;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-5b(6) — the ledger cover identifies the responsible organization.</summary>
    public string OrganizationOrUnit { get; private set; } = string.Empty;

    /// <summary>
    /// IANA/Windows time zone for display. The DA Form 4137 and the ledger record LOCAL date and
    /// time ("03 SEP 26 09:15"), so EMC must be able to render what the paper says (AUD-011).
    /// </summary>
    public string TimeZoneId { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    public void Rename(string name) => Name = Guard.NotBlank(name, "VCH-005", "Evidence room name");

    public void Deactivate() => IsActive = false;
}
