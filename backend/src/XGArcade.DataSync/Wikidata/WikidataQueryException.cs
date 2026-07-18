namespace XGArcade.DataSync.Wikidata;

// Thrown ONLY by IWikidataClient.QueryPlayerPoolBirthYearAsync (the bulk
// name-index import path) on timeout/HTTP/parse failure — so the importer
// can distinguish "this slice failed, retry it and fail the run loudly if
// it keeps failing" from "this birth year genuinely has no eligible
// players" (an empty list). Do NOT adopt this for the intersection queries:
// their swallow-to-[] contract is load-bearing (REQ-103's "never block grid
// generation on a Wikidata failure") — see WikidataClient's per-method
// comments and NOTES.md 2026-07-18 for the incident that forced this split.
public class WikidataQueryException : Exception
{
    public WikidataQueryException(string message)
        : base(message)
    {
    }

    public WikidataQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
