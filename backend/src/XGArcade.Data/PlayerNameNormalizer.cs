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
    // Non-decomposable Latin letters (bug fix, 2026-08-02, reported via xG
    // Path user testing): Ø/ø, Æ/æ, Œ/œ, Đ/đ, Ł/ł, ß, Þ/þ are distinct
    // Unicode code points in their own right, NOT a base letter + combining
    // diacritic mark the way é/ñ/etc. are — NFKD normalization below leaves
    // every one of them completely untouched, so the NonSpacingMark-strip
    // loop that turns "é" into "e" never gets a chance to touch them either.
    // Concretely: "Ødegaard" (Martin Ødegaard, a real player) normalized to
    // "ødegaard", which never equalled what a player actually types
    // ("Odegaard"/"Odegard" both normalize to "odegaard") — autocomplete
    // never suggested him and a correct guess scored incorrect. Mapped to
    // their standard ASCII transliteration in a pass BEFORE NFKD; running it
    // before vs. after makes no functional difference (NFKD is a documented
    // no-op on these code points either way) but keeps this fix textually
    // next to — and conceptually part of — the "strip diacritics" step the
    // class-level comment above already describes, rather than reading like
    // a bolted-on third step. Replacement case is irrelevant: ToLowerInvariant
    // runs at the very end regardless of what case is produced here.
    //
    // Deliberately narrow: this maps distinct LETTERS to their ASCII
    // transliteration, nothing else — it must never grow into fuzzy/edit-
    // distance typo tolerance (that's REQ-208's deliberately-deferred scope
    // per MVP-SCOPE.md). "Oodegaard" (an extra vowel, a genuine typo) is NOT
    // in this map and must keep normalizing to something different from
    // "Odegaard" — see PlayerNameNormalizerTests for the regression case
    // that pins this distinction down.
    private static readonly Dictionary<char, string> NonDecomposableLetterMap = new()
    {
        ['Ø'] = "O", ['ø'] = "o",
        ['Æ'] = "AE", ['æ'] = "ae",
        ['Œ'] = "OE", ['œ'] = "oe",
        ['Đ'] = "D", ['đ'] = "d",
        ['Ł'] = "L", ['ł'] = "l",
        ['ß'] = "ss",
        ['Þ'] = "Th", ['þ'] = "th",
    };

    public static string Normalize(string value)
    {
        var transliterated = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (NonDecomposableLetterMap.TryGetValue(c, out var replacement))
                transliterated.Append(replacement);
            else
                transliterated.Append(c);
        }

        var decomposed = transliterated.ToString().Normalize(NormalizationForm.FormKD);

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
