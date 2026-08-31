namespace XGArcade.DataSync.FootballData;

// Mirrors WikidataQueryException's shape (message + optional inner
// exception) — thrown by IFootballDataClient on HTTP failure, non-success
// status, timeout, or unparseable/unexpected JSON. Both of this client's
// methods are job-style/server-side fetches whose success metric matters
// (never a per-request/per-user path) — a swallowed failure here would be
// indistinguishable from a genuine empty result (REQ-1301's "this
// gameweek genuinely has fewer than 5 fixtures") or from REQ-1305's "not
// yet confirmed" retry-later state, so this client never swallows a
// technical failure to an empty/default value. Same reasoning as
// WikidataClient's throwing query methods and this client's predecessor,
// ApiFootballClientException (ADR-0094, superseded by ADR-0099).
public class FootballDataClientException : Exception
{
    public FootballDataClientException(string message)
        : base(message)
    {
    }

    public FootballDataClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
