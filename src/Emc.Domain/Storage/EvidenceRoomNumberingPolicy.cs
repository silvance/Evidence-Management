using System.Globalization;
using Emc.Domain.Common;

namespace Emc.Domain.Storage;

/// <summary>How the two groups of digits are laid out in a written document number.</summary>
public enum DocumentNumberLayout
{
    /// <summary>AR 195-5 2-4c: sequence, hyphen, two-digit year - "001-26".</summary>
    SequenceThenYear = 1,

    /// <summary>A LOCAL layout some evidence rooms use - "26-01". Not described by AR 195-5.</summary>
    YearThenSequence = 2
}

/// <summary>On what authority a room writes its document numbers the way it does.</summary>
public enum NumberingPolicyBasis
{
    /// <summary>The layout AR 195-5 2-4c prescribes. Needs no further authority.</summary>
    RegulationDefault = 1,

    /// <summary>A layout that differs from 2-4c, adopted under a cited local SOP, policy, waiver or directive.</summary>
    LocalAuthorized = 2,

    /// <summary>
    /// A layout that differs from 2-4c, observed in the room's existing ledger, with no authority
    /// yet cited. Recorded so existing numbers can be entered as written, and FLAGGED as a local
    /// procedure awaiting validation on every use.
    /// </summary>
    LegacyObserved = 3
}

/// <summary>
/// How an evidence room writes its evidence document numbers, effective-dated.
///
/// AR 195-5 2-4c prescribes the layout: "two groups of digits, separated by a hyphen. The first
/// group is the number of the document beginning with the number 001 for the first DA Form 4137
/// received for the calendar year; the second group will represent the current calendar year
/// (for example, 001-18)." That layout is the DEFAULT and needs no authority.
///
/// Some rooms write the year first - "26-01". AR 195-5 does not describe that layout and EMC
/// must not claim it does. But a companion system (2-5c) that refused to record the number a
/// custodian actually wrote in the ledger would be useless in such a room, so the layout is a
/// per-room, effective-dated policy [LOCAL], and a layout other than the regulation's must
/// either cite its authority or be flagged as awaiting validation.
///
/// The layout is PRESENTATION. The identity of a document number is
/// (EvidenceRoom, CalendarYear, Sequence), and it is that canonical form that is unique and that
/// is compared - so "001-26" recorded under one policy and "26-01" recorded under a later one
/// are the same number and the second is refused (VCH-011). The number as written is preserved
/// verbatim on each assignment.
///
/// Structured, not a regex. A user-supplied pattern could express layouts nobody intended and
/// could not be reasoned about here.
///
/// Requirements: VCH-004, VCH-022, VCH-023.
/// </summary>
public class EvidenceRoomNumberingPolicy : Entity, IConcurrencyStamped
{
    public const int RegulatorySequenceWidth = 3;
    public const int RegulatoryYearWidth = 2;
    public const string RegulatorySeparator = "-";

    private EvidenceRoomNumberingPolicy() { }

    public EvidenceRoomNumberingPolicy(
        int evidenceRoomId,
        DateTimeOffset effectiveFrom,
        DocumentNumberLayout layout,
        int sequenceWidth,
        int yearWidth,
        string separator,
        NumberingPolicyBasis basis,
        string? authorityReference,
        string? notes,
        DateTimeOffset? effectiveTo = null)
    {
        if (sequenceWidth is < 1 or > 6)
        {
            throw new DomainRuleViolationException(
                "VCH-023", "The sequence width must be between 1 and 6 digits.");
        }

        if (yearWidth is not (2 or 4))
        {
            throw new DomainRuleViolationException(
                "VCH-023", "The year is written with either 2 or 4 digits.");
        }

        var sep = Guard.NotBlank(separator, "VCH-023", "Separator");
        if (sep.Length > 3 || sep.Any(char.IsDigit))
        {
            throw new DomainRuleViolationException(
                "VCH-023", "The separator must be one to three non-digit characters.");
        }

        if (effectiveTo is not null && effectiveTo <= effectiveFrom)
        {
            throw new DomainRuleViolationException(
                "VCH-023", "A numbering policy cannot end before it takes effect.");
        }

        EvidenceRoomId = evidenceRoomId;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        Layout = layout;
        SequenceWidth = sequenceWidth;
        YearWidth = yearWidth;
        Separator = sep;
        Basis = basis;
        AuthorityReference = Guard.TrimToNull(authorityReference);
        Notes = Guard.TrimToNull(notes);
        ConcurrencyStamp = Guid.NewGuid();

        // The basis must be honest about the layout. The regulation's own layout is the only one
        // that may claim RegulationDefault; anything else is local, and local means either a
        // cited authority or an explicit flag that none has been cited yet.
        if (basis == NumberingPolicyBasis.RegulationDefault && !IsRegulatoryLayout)
        {
            throw new DomainRuleViolationException(
                "VCH-023",
                "AR 195-5 para 2-4c prescribes a three-digit sequence, a hyphen, then the "
                + "two-digit calendar year (001-26). A different layout cannot be recorded as the "
                + "regulation default. Record it as locally authorized with the authority cited, "
                + "or as a legacy practice awaiting validation.");
        }

        if (basis == NumberingPolicyBasis.LocalAuthorized && AuthorityReference is null)
        {
            throw new DomainRuleViolationException(
                "VCH-023",
                "A locally authorized numbering layout must cite the SOP, policy, waiver or "
                + "directive that authorizes it. If none can be cited, record the layout as a "
                + "legacy practice awaiting validation instead.");
        }
    }

    public int EvidenceRoomId { get; private set; }
    public EvidenceRoom? EvidenceRoom { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }

    public DocumentNumberLayout Layout { get; private set; }

    /// <summary>Minimum digits in the sequence group, zero-padded. A sequence may exceed it (1000-26).</summary>
    public int SequenceWidth { get; private set; }

    /// <summary>Digits in the year group: 2 as the regulation writes it, or 4.</summary>
    public int YearWidth { get; private set; }

    public string Separator { get; private set; } = RegulatorySeparator;

    public NumberingPolicyBasis Basis { get; private set; }

    /// <summary>The local SOP, policy, waiver or directive, when the layout is not the regulation's.</summary>
    public string? AuthorityReference { get; private set; }

    public string? Notes { get; private set; }

    public Guid ConcurrencyStamp { get; set; }

    /// <summary>True when this is exactly the layout AR 195-5 2-4c describes.</summary>
    public bool IsRegulatoryLayout
        => Layout == DocumentNumberLayout.SequenceThenYear
           && SequenceWidth == RegulatorySequenceWidth
           && YearWidth == RegulatoryYearWidth
           && Separator == RegulatorySeparator;

    /// <summary>A local layout with no authority cited yet. Flagged on every use.</summary>
    public bool IsAwaitingValidation => Basis == NumberingPolicyBasis.LegacyObserved;

    public bool IsEffectiveAt(DateTimeOffset at)
        => EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);

    /// <summary>The layout AR 195-5 2-4c prescribes.</summary>
    public static EvidenceRoomNumberingPolicy Regulatory(int evidenceRoomId, DateTimeOffset effectiveFrom)
        => new(
            evidenceRoomId, effectiveFrom,
            DocumentNumberLayout.SequenceThenYear,
            RegulatorySequenceWidth, RegulatoryYearWidth, RegulatorySeparator,
            NumberingPolicyBasis.RegulationDefault,
            authorityReference: null,
            notes: "AR 195-5 para 2-4c.");

    /// <summary>Closes this policy so a successor can take effect.</summary>
    public void EndAt(DateTimeOffset effectiveTo)
    {
        if (effectiveTo <= EffectiveFrom)
        {
            throw new DomainRuleViolationException(
                "VCH-023", "A numbering policy cannot end before it takes effect.");
        }

        EffectiveTo = effectiveTo;
    }

    /// <summary>Writes a canonical number the way this room writes it.</summary>
    public string Format(int sequence, int calendarYear)
    {
        Guard.Positive(sequence, "VCH-004", "Sequence");

        var seq = sequence.ToString(CultureInfo.InvariantCulture).PadLeft(SequenceWidth, '0');
        var year = YearWidth == 4
            ? calendarYear.ToString("D4", CultureInfo.InvariantCulture)
            : (calendarYear % 100).ToString("D2", CultureInfo.InvariantCulture);

        return Layout == DocumentNumberLayout.SequenceThenYear
            ? seq + Separator + year
            : year + Separator + seq;
    }

    /// <summary>
    /// Reads the two groups from a number written under this layout. The year group must be
    /// exactly <see cref="YearWidth"/> digits; the sequence group at least
    /// <see cref="SequenceWidth"/>. Century resolution is not done here - see
    /// <see cref="Emc.Domain.Cases.EvidenceDocumentNumber"/>.
    /// </summary>
    public bool TryParseComponents(string? raw, out int sequence, out int yearDigits)
    {
        sequence = 0;
        yearDigits = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        var parts = text.Split(Separator, StringSplitOptions.None);
        if (parts.Length != 2)
        {
            return false;
        }

        var (seqText, yearText) = Layout == DocumentNumberLayout.SequenceThenYear
            ? (parts[0], parts[1])
            : (parts[1], parts[0]);

        if (yearText.Length != YearWidth || !yearText.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (seqText.Length < SequenceWidth || !seqText.All(char.IsAsciiDigit))
        {
            return false;
        }

        sequence = int.Parse(seqText, CultureInfo.InvariantCulture);
        yearDigits = int.Parse(yearText, CultureInfo.InvariantCulture);

        // 2-4c begins the series at 001. A sequence of zero is not a document number.
        return sequence > 0;
    }

    /// <summary>A plain-language description of the layout, for validation messages and the form.</summary>
    public string Describe()
    {
        var seq = $"a {Words(SequenceWidth)}-digit sequence beginning at {1.ToString(CultureInfo.InvariantCulture).PadLeft(SequenceWidth, '0')}";
        var year = YearWidth == 4 ? "the four-digit calendar year" : "the two-digit calendar year";
        var sep = Separator == "-" ? "a hyphen" : $"\"{Separator}\"";

        var order = Layout == DocumentNumberLayout.SequenceThenYear
            ? $"{seq}, {sep}, then {year}"
            : $"{year}, {sep}, then {seq}";

        var basis = Basis switch
        {
            NumberingPolicyBasis.RegulationDefault => "AR 195-5 para 2-4c",
            NumberingPolicyBasis.LocalAuthorized => $"local policy, {AuthorityReference}; AR 195-5 para 2-4c prescribes 001-26",
            _ => "a LOCAL practice awaiting validation; AR 195-5 para 2-4c prescribes 001-26"
        };

        return $"{order} (for example {Example()}) - {basis}";
    }

    /// <summary>The 37th form of 2026, written this room's way.</summary>
    public string Example() => Format(37, 2026);

    private static string Words(int n) => n switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six",
        _ => n.ToString(CultureInfo.InvariantCulture)
    };
}
