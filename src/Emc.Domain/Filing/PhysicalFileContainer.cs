using Emc.Domain.Common;
using Emc.Domain.Storage;

namespace Emc.Domain.Filing;

/// <summary>The kinds of paper file AR 195-5 2-4f and 2-4h require an evidence room to keep.</summary>
public enum PhysicalFileKind
{
    /// <summary>AR 195-5 2-4f(1) - active DA Forms 4137, numerical sequence, no more than 50 per folder/binder.</summary>
    Active4137File = 1,

    /// <summary>AR 195-5 2-4h - inactive DA Forms 4137, labeled by month and year of disposition.</summary>
    Inactive4137File = 2,

    /// <summary>AR 195-5 2-4f(3)(a) - suspense copies for evidence sent to the laboratory.</summary>
    SuspenseUsacil = 3,

    /// <summary>AR 195-5 2-4f(3)(b) - suspense copies for evidence on temporary release for legal proceedings.</summary>
    SuspenseAdjudication = 4,

    /// <summary>AR 195-5 2-4f(3)(c) - suspense copies while the original is with trial counsel / prosecutor for disposition approval.</summary>
    SuspensePendingDispositionApproval = 5
}

/// <summary>Folder or binder. AR 195-5 2-4f(1) allows either; it makes no difference to accountability.</summary>
public enum ContainerForm
{
    Folder = 1,
    Binder = 2,
    Other = 3
}

/// <summary>
/// A physical folder, binder or file in the evidence room that holds paper DA Forms 4137.
///
/// This exists because the PHYSICAL original DA Form 4137 is an operational custody document
/// that AR 195-5 requires the custodian to keep in specific files (2-4d, 2-4f, 2-4h) and to send
/// with the evidence in specific situations (2-4f(2), 2-7g). A scan stored by EMC is not the
/// original and never stands in for it; the paper record is modelled here, separately from
/// <c>SourceDocument</c>, so the two cannot be confused.
///
/// The 50-voucher limit on an active folder/binder is the regulation's (2-4f(1)) [REG]. Whether
/// the unit uses folders or binders, and what it writes on the label beyond the document-number
/// range 2-4f(1) requires, is the unit's [LOCAL].
///
/// Requirements: FIL-001, FIL-002, FIL-003.
/// </summary>
public class PhysicalFileContainer : Entity, IConcurrencyStamped
{
    /// <summary>AR 195-5 2-4f(1): "no more than 50 vouchers ... will be contained in one folder or binder." [REG]</summary>
    public const int ActiveFileVoucherCapacity = 50;

    private PhysicalFileContainer() { }

    public PhysicalFileContainer(
        int evidenceRoomId,
        PhysicalFileKind kind,
        ContainerForm form,
        string label,
        string? documentNumberRangeFrom = null,
        string? documentNumberRangeTo = null,
        int? dispositionYear = null,
        int? dispositionMonth = null,
        string? notes = null)
    {
        EvidenceRoomId = evidenceRoomId;
        Kind = kind;
        Form = form;
        Label = Guard.NotBlank(label, "FIL-001", "Container label");
        DocumentNumberRangeFrom = Guard.TrimToNull(documentNumberRangeFrom);
        DocumentNumberRangeTo = Guard.TrimToNull(documentNumberRangeTo);
        Notes = Guard.TrimToNull(notes);
        IsActive = true;
        ConcurrencyStamp = Guid.NewGuid();

        if (kind == PhysicalFileKind.Inactive4137File)
        {
            // AR 195-5 2-4h: "This inactive file will be labeled by month and year of the
            // disposition date."
            if (dispositionYear is null || dispositionMonth is null)
            {
                throw new DomainRuleViolationException(
                    "FIL-003",
                    "AR 195-5 para 2-4h: an inactive DA Form 4137 file is labeled by the month and "
                    + "year of the disposition date. State both.");
            }

            if (dispositionMonth is < 1 or > 12 || dispositionYear is < 1990 or > 2200)
            {
                throw new DomainRuleViolationException("FIL-003", "The disposition month and year are not valid.");
            }

            DispositionYear = dispositionYear;
            DispositionMonth = dispositionMonth;
        }
        else if (dispositionYear is not null || dispositionMonth is not null)
        {
            throw new DomainRuleViolationException(
                "FIL-003", "Only an inactive DA Form 4137 file carries a disposition month and year.");
        }
    }

    public int EvidenceRoomId { get; private set; }
    public EvidenceRoom? EvidenceRoom { get; private set; }

    public PhysicalFileKind Kind { get; private set; }
    public ContainerForm Form { get; private set; }

    /// <summary>What is written on the outside. For an active file, 2-4f(1) requires the number range.</summary>
    public string Label { get; private set; } = string.Empty;

    /// <summary>AR 195-5 2-4f(1) - the range of document numbers "shown on the outside", as written.</summary>
    public string? DocumentNumberRangeFrom { get; private set; }

    public string? DocumentNumberRangeTo { get; private set; }

    /// <summary>AR 195-5 2-4h - inactive files only.</summary>
    public int? DispositionYear { get; private set; }

    public int? DispositionMonth { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>False once the container is closed (full, or retired). A closed container accepts no filing.</summary>
    public bool IsActive { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    public bool IsSuspense
        => Kind is PhysicalFileKind.SuspenseUsacil
            or PhysicalFileKind.SuspenseAdjudication
            or PhysicalFileKind.SuspensePendingDispositionApproval;

    /// <summary>
    /// AR 195-5 2-4f(1). The count is of vouchers currently filed here, supplied by the caller
    /// from the store; the container does not hold the list.
    /// </summary>
    public void AssertCanAcceptAnotherVoucher(int currentlyFiledCount)
    {
        if (!IsActive)
        {
            throw new DomainRuleViolationException(
                "FIL-001", $"File container \"{Label}\" is closed and accepts no further filing.");
        }

        if (Kind == PhysicalFileKind.Active4137File && currentlyFiledCount >= ActiveFileVoucherCapacity)
        {
            throw new DomainRuleViolationException(
                "FIL-002",
                $"AR 195-5 para 2-4f(1): no more than {ActiveFileVoucherCapacity} vouchers with "
                + $"attached documents are contained in one folder or binder. \"{Label}\" already "
                + $"holds {currentlyFiledCount}. Open the next active file.");
        }
    }

    public void Relabel(string label, string? rangeFrom, string? rangeTo)
    {
        Label = Guard.NotBlank(label, "FIL-001", "Container label");
        DocumentNumberRangeFrom = Guard.TrimToNull(rangeFrom);
        DocumentNumberRangeTo = Guard.TrimToNull(rangeTo);
    }

    public void Close() => IsActive = false;

    /// <summary>The 2-4h label, "SEP 2026", for an inactive file.</summary>
    public string? DispositionLabel
        => DispositionYear is null || DispositionMonth is null
            ? null
            : new DateOnly(DispositionYear.Value, DispositionMonth.Value, 1).ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
}
