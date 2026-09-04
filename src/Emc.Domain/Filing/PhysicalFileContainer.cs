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

/// <summary>
/// Folder or binder. AR 195-5 2-4f(1): an active file is "a file folder or binder"; nothing
/// else is authorized for an active file. Other is for suspense and inactive files only, where
/// the regulation names no form.
/// </summary>
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
/// original and never stands in for it.
///
/// An ACTIVE file [REG 2-4f(1)]: a folder or binder; in numerical sequence; no more than 50
/// vouchers; the number and year of the documents shown on the outside. The range is held here
/// canonically (calendar year, first and last sequence) so that a voucher is filed only into the
/// binder whose range covers its number, whatever layout the room writes numbers in (VCH-023);
/// the rendered range is the label text. A range of at most 50 numbers follows from the limit
/// [DESIGN, derived]. One calendar year per range [DESIGN]: numbering restarts at 001 each year
/// (2-4c), so a binder spanning years would not be "in numerical sequence".
///
/// <see cref="FiledVoucherCount"/> is maintained by the domain and protected by the concurrency
/// stamp, so two custodians filing the 50th voucher at once cannot both succeed: the second save
/// conflicts and is refused (FIL-002).
///
/// Requirements: FIL-001, FIL-002, FIL-003, FIL-011, FIL-012, FIL-013.
/// </summary>
public class PhysicalFileContainer : Entity, IConcurrencyStamped
{
    /// <summary>AR 195-5 2-4f(1): "no more than 50 vouchers with attached documents per folder/binder." [REG]</summary>
    public const int ActiveFileVoucherCapacity = 50;

    private PhysicalFileContainer() { }

    public PhysicalFileContainer(
        int evidenceRoomId,
        PhysicalFileKind kind,
        ContainerForm form,
        string label,
        int? rangeCalendarYear = null,
        int? rangeFromSequence = null,
        int? rangeToSequence = null,
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
        Notes = Guard.TrimToNull(notes);
        IsActive = true;
        ConcurrencyStamp = Guid.NewGuid();

        if (kind == PhysicalFileKind.Active4137File)
        {
            if (form is not (ContainerForm.Folder or ContainerForm.Binder))
            {
                throw new DomainRuleViolationException(
                    "FIL-011", "AR 195-5 para 2-4f(1): an active DA Form 4137 file is a file folder or a binder.");
            }

            if (rangeCalendarYear is null || rangeFromSequence is null || rangeToSequence is null)
            {
                throw new DomainRuleViolationException(
                    "FIL-012",
                    "AR 195-5 para 2-4f(1): the number and year of the documents in the folder/binder are shown "
                    + "on the outside. State the calendar year and the first and last document sequence.");
            }

            if (rangeCalendarYear is < 1990 or > 2200 || rangeFromSequence < 1 || rangeToSequence < rangeFromSequence)
            {
                throw new DomainRuleViolationException("FIL-012", "The document-number range is not valid.");
            }

            if (rangeToSequence - rangeFromSequence + 1 > ActiveFileVoucherCapacity)
            {
                throw new DomainRuleViolationException(
                    "FIL-012",
                    $"AR 195-5 para 2-4f(1): a folder/binder holds no more than {ActiveFileVoucherCapacity} vouchers, "
                    + $"so its range covers at most {ActiveFileVoucherCapacity} numbers.");
            }

            RangeCalendarYear = rangeCalendarYear;
            RangeFromSequence = rangeFromSequence;
            RangeToSequence = rangeToSequence;
            DocumentNumberRangeFrom = Guard.NotBlank(documentNumberRangeFrom, "FIL-012", "Rendered range start");
            DocumentNumberRangeTo = Guard.NotBlank(documentNumberRangeTo, "FIL-012", "Rendered range end");
        }
        else if (rangeCalendarYear is not null || rangeFromSequence is not null || rangeToSequence is not null)
        {
            throw new DomainRuleViolationException("FIL-012", "Only an active DA Form 4137 file carries a document-number range.");
        }

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

    /// <summary>What is written on the outside.</summary>
    public string Label { get; private set; } = string.Empty;

    /// <summary>Active files: the canonical range (2-4f(1)), one calendar year, first and last sequence.</summary>
    public int? RangeCalendarYear { get; private set; }
    public int? RangeFromSequence { get; private set; }
    public int? RangeToSequence { get; private set; }

    /// <summary>The range as the room writes it (its numbering layout), for the label.</summary>
    public string? DocumentNumberRangeFrom { get; private set; }
    public string? DocumentNumberRangeTo { get; private set; }

    /// <summary>AR 195-5 2-4h - inactive files only.</summary>
    public int? DispositionYear { get; private set; }
    public int? DispositionMonth { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>False once the container is closed (full, or retired). A closed container accepts no filing.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Vouchers whose paper is in this container NOW. Maintained by the domain; guarded by the stamp.</summary>
    public int FiledVoucherCount { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    public bool IsSuspense
        => Kind is PhysicalFileKind.SuspenseUsacil
            or PhysicalFileKind.SuspenseAdjudication
            or PhysicalFileKind.SuspensePendingDispositionApproval;

    public bool Covers(int sequence, int calendarYear)
        => Kind == PhysicalFileKind.Active4137File
           && RangeCalendarYear == calendarYear
           && sequence >= RangeFromSequence && sequence <= RangeToSequence;

    /// <summary>FIL-012. A voucher is filed only in the binder whose range covers its number.</summary>
    public void AssertCoversDocumentNumber(int sequence, int calendarYear)
    {
        if (!Covers(sequence, calendarYear))
        {
            throw new DomainRuleViolationException(
                "FIL-012",
                $"AR 195-5 para 2-4f(1): active DA Forms 4137 are filed in numerical sequence in the folder/binder "
                + $"whose range is shown on the outside. \"{Label}\" covers {DocumentNumberRangeFrom} through "
                + $"{DocumentNumberRangeTo}; this voucher is sequence {sequence} of {calendarYear}.");
        }
    }

    /// <summary>FIL-013. An inactive file receives only records that became inactive in its labelled month and year (2-4h).</summary>
    public void AssertLabeledForDispositionDate(DateTimeOffset dispositionDate)
    {
        if (Kind != PhysicalFileKind.Inactive4137File)
        {
            return;
        }

        if (DispositionYear != dispositionDate.Year || DispositionMonth != dispositionDate.Month)
        {
            throw new DomainRuleViolationException(
                "FIL-013",
                $"AR 195-5 para 2-4h: the inactive file is labelled by the month and year of the disposition date. "
                + $"\"{Label}\" is {DispositionLabel}; this record became inactive in "
                + $"{dispositionDate.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()}.");
        }
    }

    /// <summary>
    /// One more voucher's paper is now here. Refuses a closed container and, for an active file,
    /// the 51st voucher (2-4f(1)). Bumps the concurrency stamp so a concurrent filing conflicts
    /// at the database instead of both becoming the 50th.
    /// </summary>
    public void RecordFiled()
    {
        if (!IsActive)
        {
            throw new DomainRuleViolationException(
                "FIL-001", $"File container \"{Label}\" is closed and accepts no further filing.");
        }

        if (Kind == PhysicalFileKind.Active4137File && FiledVoucherCount >= ActiveFileVoucherCapacity)
        {
            throw new DomainRuleViolationException(
                "FIL-002",
                $"AR 195-5 para 2-4f(1): no more than {ActiveFileVoucherCapacity} vouchers with "
                + $"attached documents are contained in one folder or binder. \"{Label}\" already "
                + $"holds {FiledVoucherCount}. Open the next active file.");
        }

        FiledVoucherCount++;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>One voucher's paper has left this container.</summary>
    public void RecordRemoved()
    {
        if (FiledVoucherCount <= 0)
        {
            throw new DomainRuleViolationException("FIL-001", $"File container \"{Label}\" records no filed vouchers to remove.");
        }

        FiledVoucherCount--;
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Relabel(string label)
    {
        Label = Guard.NotBlank(label, "FIL-001", "Container label");
        ConcurrencyStamp = Guid.NewGuid();
    }

    public void Close()
    {
        IsActive = false;
        ConcurrencyStamp = Guid.NewGuid();
    }

    /// <summary>The 2-4h label, "SEP 2026", for an inactive file.</summary>
    public string? DispositionLabel
        => DispositionYear is null || DispositionMonth is null
            ? null
            : new DateOnly(DispositionYear.Value, DispositionMonth.Value, 1).ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
}
