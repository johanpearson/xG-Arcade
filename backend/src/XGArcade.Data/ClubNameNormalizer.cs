namespace XGArcade.Data;

// Extracted (2026-09-04, REQ-1406/1407 bug fix) from
// XGArcade.DataSync.Wikidata.SparqlResponseParsers.NormalizeClubName, which
// used this suffix-stripping to canonicalize a club name once, at Wikidata
// ingest time. Promoted to XGArcade.Data — alongside PlayerNameNormalizer,
// the same "shared normalization function used wherever a name needs
// comparing" precedent — because xG Connect's chain-step club-claim
// comparison (PlayerCareerOverlapService.HaveOverlapAtClubAsync,
// Games.XGConnect) needs the exact same normalization applied to a player-
// typed club name at QUERY time, not just at ingest time: a genuinely
// correct claim like "Chelsea FC" was being rejected because the stored,
// ingest-time-normalized value is "Chelsea" and the comparison was a bare
// case-insensitive string equality with no suffix-stripping on the
// player-typed side.
public static class ClubNameNormalizer
{
    // Legal-suffix variants Wikidata is observed to use interchangeably for
    // what is the same real club (e.g. "Liverpool" vs "Liverpool F.C.",
    // both attested as ?clubLabel values for the same P54 statement shape).
    // Ordered longest-first so a longer variant (e.g. "A.F.C.") is matched
    // whole rather than partially matching a shorter entry later in the
    // list ("F.C.") first.
    //
    // Deliberately a small, explicit list, not a fuzzy/generic name
    // matcher: a generic matcher risks merging two DIFFERENT clubs that
    // happen to share a prefix (e.g. stripping too aggressively could
    // conflate "Real Madrid" and "Real Sociedad"-style near-collisions).
    // This only ever strips one of these four exact, well-known football
    // legal-suffix tokens, and only when it is the trailing token of the
    // name (preceded by whitespace) — never a substring inside an
    // unrelated word, and never a PREFIX (e.g. "AFC Bournemouth" is a
    // different, legitimate naming convention and is left untouched).
    //
    // Single-pass, not recursive: only ONE trailing suffix is ever
    // stripped, so a hypothetical stacked label like "Club FC A.F.C."
    // would only lose the first match ("A.F.C.") and come back as
    // "Club FC", not "Club". Judged acceptable given this is a narrow,
    // 4-entry list of real football legal suffixes -- a doubly-suffixed
    // label has not been observed and is not expected in practice.
    private static readonly string[] ClubNameLegalSuffixes = ["A.F.C.", "F.C.", "AFC", "FC"];

    public static string StripLegalSuffix(string rawClubName)
    {
        var trimmed = rawClubName.Trim();

        foreach (var suffix in ClubNameLegalSuffixes)
        {
            if (trimmed.Length <= suffix.Length)
                continue;

            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Must be a distinct trailing TOKEN — the character right
            // before the suffix must be whitespace, or this would also
            // strip "FC" out of the middle/end of an unrelated single
            // word.
            if (!char.IsWhiteSpace(trimmed[trimmed.Length - suffix.Length - 1]))
                continue;

            return trimmed[..^suffix.Length].TrimEnd();
        }

        return trimmed;
    }
}
