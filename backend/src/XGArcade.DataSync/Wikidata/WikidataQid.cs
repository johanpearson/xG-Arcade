using System.Text.RegularExpressions;

namespace XGArcade.DataSync.Wikidata;

// Single source of truth for "is this string a syntactically valid Wikidata
// QID" (e.g. "Q1519") — extracted so WikidataClient's own argument
// validation (hand-curated QIDs from CategoryValueRepository/
// PlayerNameIndexImporter, where a bad QID is a caller bug worth throwing
// loudly for — see QueryCountryClubIntersectionAsync/QueryClubClubIntersectionAsync/
// QueryPlayerPhotosByQidsAsync's own ArgumentException checks, all unchanged)
// and PlayerPhotoBackfillService's pre-filter (arbitrary Player.WikidataQid
// rows read back from the database, where a malformed value is a
// data-quality issue to skip-and-log rather than a reason to crash or fail
// the whole batch — see that service's own comment) can never silently
// diverge on what "valid" means.
public static partial class WikidataQid
{
    public static bool IsValid(string qid) => Pattern().IsMatch(qid);

    [GeneratedRegex(@"^Q\d+$")]
    private static partial Regex Pattern();
}
