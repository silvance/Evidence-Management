using Emc.Domain.Common;

namespace Emc.Domain.Cases;

public enum VoucherFormRevisionKind
{
    /// <summary>AR 195-5 2-4a - the form as first submitted to the custodian.</summary>
    InitialSubmission = 1,

    /// <summary>AR 195-5 2-3g - the form as resubmitted after the agent corrected and initialed it.</summary>
    Resubmission = 2
}

/// <summary>
/// What a DA Form 4137 CONTAINED each time it went before the evidence custodian.
///
/// AR 195-5 2-3g has the custodian return the form and the submitting agent "correct and initial
/// all errors". After a return the current form legitimately differs from what was submitted:
/// a description is fixed, a serial number corrected, a line that was entered in error lined
/// through, an omitted item added. EMC must show BOTH - what was submitted, and what the corrected
/// form now contains - without pretending the obsolete line is still part of the current form,
/// and without erasing it.
///
/// So the current composition lives on the voucher's items (a withdrawn line is an item in the
/// terminal state WithdrawnAsEnteredInError), and each submission takes an immutable snapshot of
/// the lines as they read at that moment. Revision 1 is the form as first submitted; revision 2
/// is the corrected form as resubmitted; and so on.
///
/// This is a pre-acceptance form-review record. It is NOT the ledger-correction procedure of
/// 2-5b(5), which governs erroneous entries in the EVIDENCE LEDGER, and it is NOT a 1-7c(3)
/// custodian correction of an accepted accountability record. Neither an MFR nor a strike-through
/// rule is imported here; 2-3g asks for corrected and initialed, and the agent attests to that.
///
/// Append-only. Requirements: VCH-025, VCH-026.
/// </summary>
public class VoucherFormRevision : Entity, IAppendOnly
{
    private readonly List<VoucherFormRevisionLine> _lines = [];

    private VoucherFormRevision() { }

    internal VoucherFormRevision(
        EvidenceVoucher voucher,
        int revisionNumber,
        VoucherFormRevisionKind kind,
        int submittedByUserId,
        DateTimeOffset submittedAtUtc,
        IEnumerable<EvidenceItem> currentLines)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        ArgumentNullException.ThrowIfNull(currentLines);

        VoucherId = voucher.Id;
        Voucher = voucher;
        RevisionNumber = Guard.Positive(revisionNumber, "VCH-025", "Revision number");
        Kind = kind;
        SubmittedByUserId = submittedByUserId;
        SubmittedAtUtc = AccountabilityTime.Normalize(submittedAtUtc);

        foreach (var item in currentLines.OrderBy(i => i.ItemNumber))
        {
            _lines.Add(new VoucherFormRevisionLine(this, item));
        }
    }

    public int VoucherId { get; private set; }
    public EvidenceVoucher? Voucher { get; private set; }

    /// <summary>1 for the initial submission, incrementing with each resubmission.</summary>
    public int RevisionNumber { get; private set; }

    public VoucherFormRevisionKind Kind { get; private set; }
    public int SubmittedByUserId { get; private set; }
    public DateTimeOffset SubmittedAtUtc { get; private set; }

    /// <summary>The lines as they read on this revision, in item-number order.</summary>
    public IReadOnlyList<VoucherFormRevisionLine> Lines => _lines.AsReadOnly();
}

/// <summary>One line of a form revision: the item's descriptive fields as they read at submission.</summary>
public class VoucherFormRevisionLine : Entity, IAppendOnly
{
    private VoucherFormRevisionLine() { }

    internal VoucherFormRevisionLine(VoucherFormRevision revision, EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(item);

        RevisionId = revision.Id;
        Revision = revision;
        EvidenceItemId = item.Id;
        EvidenceItem = item;
        LineNumber = item.ItemNumber;
        Description = item.Description;
        Quantity = item.Quantity;
        SerialNumber = item.SerialNumber;
        UniqueDeviceIdentifier = item.UniqueDeviceIdentifier;
        IsPossibleBiohazard = item.IsPossibleBiohazard;
        IsSealed = item.IsSealed;
    }

    public int RevisionId { get; private set; }
    public VoucherFormRevision? Revision { get; private set; }

    /// <summary>The item this line describes. The item outlives revisions; the snapshot does not change.</summary>
    public int EvidenceItemId { get; private set; }
    public EvidenceItem? EvidenceItem { get; private set; }

    public int LineNumber { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string? Quantity { get; private set; }
    public string? SerialNumber { get; private set; }
    public string? UniqueDeviceIdentifier { get; private set; }
    public bool IsPossibleBiohazard { get; private set; }
    public bool IsSealed { get; private set; }
}
