namespace XGArcade.DataSync.ApiFootball;

// Mirrors WikidataQueryException's shape (message + optional inner
// exception) — thrown by IApiFootballClient on HTTP failure, non-success
// status, timeout, or unparseable/unexpected JSON. Both of this client's
// methods are job-style/server-side fetches whose success metric matters
// (never a per-request/per-user path, ADR-0094's "For AI agents" section)
// — a swallowed failure here would be indistinguishable from a genuine
// empty result (REQ-1301's "this round genuinely has fewer than 5
// fixtures") or from REQ-1305's "not yet confirmed" retry-later state, so
// this client never swallows a technical failure to an empty/default
// value. Same reasoning as WikidataClient's throwing query methods (see
// WikidataQueryException's own doc comment) — never adopted by the
// swallow-to-[] Wikidata intersection queries, adopted here unconditionally
// since this client has no equivalent "never block" caller.
public class ApiFootballClientException : Exception
{
    public ApiFootballClientException(string message)
        : base(message)
    {
    }

    public ApiFootballClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
