using Emc.Domain.Common;

namespace Emc.Domain.Cases;

/// <summary>
/// A CI investigation, identified by its case control number.
///
/// AR 195-5 2-3b: "The first DALEO or Army CI agent who seizes evidence will ensure that the law
/// enforcement report number, or Army CI case control number, is recorded on the DA Form 4137
/// and DA Form 4002."
///
/// Requirements: CASE-001, CASE-003.
/// </summary>
public class Case : Entity, IConcurrencyStamped
{
    private readonly List<EvidenceVoucher> _vouchers = [];

    private Case() { }

    public Case(
        string caseControlNumber,
        string title,
        int evidenceRoomId,
        int createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        CaseControlNumber = Guard.NotBlank(caseControlNumber, "CASE-001", "Case control number");
        Title = Guard.NotBlank(title, "CASE-003", "Case title");
        EvidenceRoomId = evidenceRoomId;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>AR 195-5 2-3b — the Army CI case control number.</summary>
    public string CaseControlNumber { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;
    public string? Synopsis { get; private set; }

    public int EvidenceRoomId { get; private set; }
    public int CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// AR 195-5 4-1a and 2-6h route classified evidence to AR 380-5. EMC invents no classified
    /// requirements; this marking is a design control supporting the classification boundary
    /// described in docs/architecture.md §9. The system's accredited level is open decision
    /// DEC-06 and must be settled before EMC holds real data.
    /// </summary>
    public string ClassificationMarking { get; private set; } = "UNCLASSIFIED";

    public bool IsClosed { get; private set; }
    public Guid ConcurrencyStamp { get; set; }

    public IReadOnlyCollection<EvidenceVoucher> Vouchers => _vouchers.AsReadOnly();

    public void UpdateDetails(string title, string? synopsis)
    {
        Title = Guard.NotBlank(title, "CASE-003", "Case title");
        Synopsis = Guard.TrimToNull(synopsis);
    }

    public void SetClassificationMarking(string marking)
        => ClassificationMarking = Guard.NotBlank(marking, "SEC-003", "Classification marking");

    public void Close() => IsClosed = true;

    public void Reopen() => IsClosed = false;
}
