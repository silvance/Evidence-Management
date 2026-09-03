using System.Text;

namespace Emc.Application.Ocr.DaForm4137;

/// <summary>Tolerant matching of printed form labels against OCR output.</summary>
internal static class TextMatching
{
    /// <summary>Upper-case letters and digits only; everything else dropped. "ITEM NO." and "ITEM N0," meet here.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>A label word matches an OCR word when they are within an edit distance that scales with length (1 for short words, ~25% for long).</summary>
    public static bool WordMatches(string labelWord, string ocrWord)
    {
        var a = Normalize(labelWord);
        var b = Normalize(ocrWord);
        if (a.Length == 0 || b.Length == 0) return false;
        var allowed = Math.Max(1, a.Length / 4);
        return Levenshtein(a, b) <= allowed;
    }

    /// <summary>
    /// Finds the label phrase inside a line's words; returns the index range of the BEST
    /// matching span, or null. Tolerant of the engine splitting or merging tokens ("ITEMNO."
    /// for "ITEM NO."): the phrase's letters and digits, concatenated, are compared against the
    /// concatenation of each candidate span of words; the span with the smallest edit distance
    /// wins (earliest on a tie), so "BY PURPOSE OF CHANGE OF CUSTODY" loses to the exact span
    /// beside it. Digits in the phrase must appear verbatim.
    /// </summary>
    public static (int Start, int End)? FindPhrase(IReadOnlyList<OcrWord> line, string phrase)
    {
        var target = Normalize(phrase);
        if (target.Length == 0 || line.Count == 0) return null;
        var labelWordCount = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var digits = new string(target.Where(char.IsDigit).ToArray());
        var allowed = Math.Max(1, target.Length / 6);

        (int Start, int End)? best = null;
        var bestDistance = int.MaxValue;
        for (var start = 0; start < line.Count; start++)
        {
            var text = string.Empty;
            for (var end = start; end < line.Count && end - start < labelWordCount + 1; end++)
            {
                text += Normalize(line[end].Text);
                if (text.Length == 0) continue;
                if (text.Length > target.Length + allowed) break;
                if (Math.Abs(text.Length - target.Length) > allowed) continue;
                if (digits.Length > 0 && !new string(text.Where(char.IsDigit).ToArray()).Equals(digits, StringComparison.Ordinal)) continue;
                var distance = Levenshtein(text, target);
                if (distance <= allowed && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (start, end);
                    if (distance == 0) return best;
                }
            }
        }

        return best;
    }
}
