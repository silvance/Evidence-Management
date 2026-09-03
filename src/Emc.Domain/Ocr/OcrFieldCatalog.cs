using System.Text.RegularExpressions;

namespace Emc.Domain.Ocr;

/// <summary>
/// Field keys an extraction can carry, and which of them are HIGH-CONSEQUENCE (OCR-003).
///
/// Key grammar: <c>Section.Field</c> or <c>Section[n].Field</c>, e.g. <c>Header.DocumentNumber</c>,
/// <c>Item[3].SerialNumber</c>, <c>Custody[2].ReceivedByName</c>. Template mappers emit keys in
/// this grammar; the catalog decides consequence from the FIELD part alone, so a new template
/// that emits a serial number under any section still gets the mandatory-verification rule.
///
/// High-consequence fields ALWAYS require explicit verification, at any confidence: the
/// document number, the case control number, item numbers, serial numbers, IMEIs and comparable
/// identifiers, the names in custody transfers, dates and times, currency amounts, and
/// disposition information. A misread in any of them is a wrong accountability record.
/// </summary>
public static partial class OcrFieldCatalog
{
    // Header (front, top)
    public const string DocumentNumber = "Header.DocumentNumber";
    public const string CaseControlNumber = "Header.CaseControlNumber";
    public const string ReceivingActivity = "Header.ReceivingActivity";
    public const string Location = "Header.Location";
    public const string NameGradeTitleOfPersonFromWhomReceived = "Header.ReceivedFromName";
    public const string ReceivedFromIsOwner = "Header.ReceivedFromIsOwner";
    public const string ReceivedFromIsOther = "Header.ReceivedFromIsOther";
    public const string AddressOfPersonFromWhomReceived = "Header.ReceivedFromAddress";
    public const string LocationFromWhereObtained = "Header.LocationObtained";
    public const string ReasonObtained = "Header.ReasonObtained";
    public const string DateTimeObtained = "Header.DateTimeObtained";

    // Item rows: Item[n].*
    public const string ItemNumberField = "ItemNumber";
    public const string ItemQuantityField = "Quantity";
    public const string ItemDescriptionField = "Description";
    public const string ItemSerialNumberField = "SerialNumber";
    public const string ItemUniqueDeviceIdentifierField = "UniqueDeviceIdentifier";

    // Chain-of-custody rows: Custody[n].*
    public const string CustodyItemNumberField = "ItemNumber";
    public const string CustodyDateField = "Date";
    public const string CustodyReleasedByNameField = "ReleasedByName";
    public const string CustodyReceivedByNameField = "ReceivedByName";
    public const string CustodyPurposeField = "Purpose";

    // Final disposal action / disposition: Disposition.*
    public const string DispositionAction = "Disposition.Action";
    public const string DispositionDate = "Disposition.Date";
    public const string DispositionAuthority = "Disposition.Authority";

    /// <summary>A generic line of text on a page with no template mapping: Page[n].Line[k].</summary>
    public static string GenericLine(int page, int line) => $"Page[{page}].Line[{line}]";

    private static readonly HashSet<string> HighConsequenceFieldNames = new(StringComparer.Ordinal)
    {
        "DocumentNumber", "CaseControlNumber",
        "ItemNumber", "SerialNumber", "UniqueDeviceIdentifier", "Imei",
        "ReleasedByName", "ReceivedByName",
        "Date", "Time", "DateTime", "DateTimeObtained",
        "Amount", "CurrencyAmount",
        "Action", "Authority"
    };

    [GeneratedRegex(@"^(?<section>[A-Za-z]+)(\[(?<index>\d+)\])?\.(?<field>[A-Za-z]+(\[\d+\])?)$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string key) => KeyPattern().IsMatch(key);

    public static string FieldName(string key)
    {
        var m = KeyPattern().Match(key);
        if (!m.Success)
        {
            throw new ArgumentException($"'{key}' is not a field key.", nameof(key));
        }

        return m.Groups["field"].Value;
    }

    public static string Section(string key)
    {
        var m = KeyPattern().Match(key);
        return m.Success ? m.Groups["section"].Value : throw new ArgumentException($"'{key}' is not a field key.", nameof(key));
    }

    /// <summary>OCR-003. Decided from the field name; disposition fields are high-consequence whatever their name.</summary>
    public static bool IsHighConsequence(string key)
    {
        var m = KeyPattern().Match(key);
        if (!m.Success)
        {
            throw new ArgumentException($"'{key}' is not a field key.", nameof(key));
        }

        if (string.Equals(m.Groups["section"].Value, "Disposition", StringComparison.Ordinal))
        {
            return true;
        }

        var field = m.Groups["field"].Value;
        var bracket = field.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            field = field[..bracket];
        }

        return HighConsequenceFieldNames.Contains(field)
            || field.EndsWith("Amount", StringComparison.Ordinal)
            || field.EndsWith("Date", StringComparison.Ordinal)
            || field.EndsWith("Time", StringComparison.Ordinal);
    }
}
