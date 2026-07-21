namespace XGArcade.Data;

// REQ-208: the edit-distance metric behind guess-time fuzzy/typo tolerance
// (GridGameModule.FindFuzzyCandidatesAsync). Plain Levenshtein distance
// (insertion/deletion/substitution, each cost 1) — the standard, well-
// understood choice for "how many single-character edits turn one string
// into the other," and the smallest useful metric for this: REQ-208 asks
// for tolerance of "minor typos," not transposition-aware or phonetic
// matching, and Levenshtein already covers the common typo shapes (a
// dropped/doubled/substituted letter) that motivate this requirement.
// Callers are expected to pass already-normalized strings (lowercased,
// diacritics/punctuation stripped via PlayerNameNormalizer.Normalize) — this
// class has no opinion on normalization, only distance.
public static class NameEditDistance
{
    public static int Distance(string a, string b)
    {
        if (a == b)
            return 0;
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        // Classic O(n*m) DP, two-row rolling buffer — these are short
        // person-name strings (tens of characters), never worth a fancier
        // bounded/banded variant.
        var previousRow = new int[b.Length + 1];
        var currentRow = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previousRow[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(
                    Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1),
                    previousRow[j - 1] + cost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[b.Length];
    }
}
