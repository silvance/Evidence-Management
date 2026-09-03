namespace Emc.Domain.Ocr;

/// <summary>
/// [DESIGN] OCR-002 thresholds. Engine confidences are 0-100. The thresholds are deliberately
/// conservative: a form's fields are short, and a single misread character in a serial number
/// is a wrong record, so "high" begins where the engine is rarely wrong on printed text and
/// "medium" ends where a guess would mislead more than it helps. Tunable per engine in a later
/// version; changing them changes only what is PREPOPULATED, never what is authoritative.
/// </summary>
public static class ConfidenceBanding
{
    public const decimal HighThreshold = 90m;
    public const decimal MediumThreshold = 60m;

    public static ConfidenceBand Band(decimal confidence)
    {
        if (confidence < 0 || confidence > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence is a percentage.");
        }

        return confidence >= HighThreshold ? ConfidenceBand.High
             : confidence >= MediumThreshold ? ConfidenceBand.Medium
             : ConfidenceBand.LowOrUnreadable;
    }
}
