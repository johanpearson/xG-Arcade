using System.Globalization;
using System.Text;

namespace XGArcade.Data;

// normalize(s) = lowercase(strip_diacritics(NFKD(s))).trim().collapse_whitespace()
// — implementation-document.md §6's shared normalization function. Used
// wherever a name/alias needs comparing regardless of case, diacritics, or
// incidental whitespace: PlayerAlias.NormalizedAlias now (S-006, populated
// for free from Wikidata's skos:altLabel); REQ-207/208's guess-time name
// matching later (Tier 1) should reuse this rather than reimplementing it.
public static class PlayerNameNormalizer
{
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);

        var withoutDiacritics = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                withoutDiacritics.Append(c);
        }

        var lowercased = withoutDiacritics.ToString().ToLowerInvariant().Trim();
        var collapsedWhitespace = string.Join(' ', lowercased.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsedWhitespace;
    }
}
