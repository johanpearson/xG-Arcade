using System.Globalization;
using System.Text;

namespace XGArcade.Data;

// normalize(s) = lowercase(strip_diacritics(strip_punctuation(NFKD(s)))).trim().collapse_whitespace()
// — implementation-document.md §6's shared normalization function. Used
// wherever a name/alias needs comparing regardless of case, diacritics,
// punctuation, or incidental whitespace: PlayerAlias.NormalizedAlias (S-006,
// populated for free from Wikidata's skos:altLabel); REQ-207/208's guess-time
// name matching (S-009, Player.NormalizedFullName) reuses this rather than
// reimplementing it.
public static class PlayerNameNormalizer
{
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);

        var filtered = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue; // diacritic mark

            // REQ-208: punctuation is ignored, not treated as a word
            // separator — removed outright rather than replaced with a
            // space, so e.g. "O'Neil" and "ONeil" normalize identically.
            if (char.IsPunctuation(c))
                continue;

            filtered.Append(c);
        }

        var lowercased = filtered.ToString().ToLowerInvariant().Trim();
        var collapsedWhitespace = string.Join(' ', lowercased.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsedWhitespace;
    }
}
