using XGArcade.DataSync.Wikidata;

namespace XGArcade.Api.DataSync;

// Shared "a live Wikidata call failed" -> 503 Problem shape. Extracted when
// InternalRoundEndpoints.cs's REQ-509/510 E2E seed endpoint (S-245) became
// the THIRD near-identical
// `catch (WikidataQueryException ex) { logger.LogWarning(...); return
// Results.Problem(title: "Live verification unavailable", ...,
// StatusCodes.Status503ServiceUnavailable); }` block in this API, alongside
// AdminSuggestionEndpoints.cs's two /lookup endpoints — docs/coding-guidelines.md's
// Code health budget says not to wait for a fifth copy before extracting a
// third occurrence of the same block shape.
//
// Deliberately keeps each call site's own log-message template/args (they
// differ per caller: suggestion id, player name, or realPlayerName) — only
// the (log + 503 Problem) plumbing is shared, not the wording.
//
// ex.Message is never surfaced in the response body here — ADR-0046's
// timeout-vs-no-match distinction plus docs/coding-guidelines.md's
// error-handling rule: every current caller of this helper is an admin's
// browser or a Playwright spec, never the /internal/* bearer-token-gated-
// scheduled-job carve-out that endpoint's own doc comment describes. If a
// future caller genuinely is that carve-out, it should NOT use this helper
// (it would need to surface ex.Message, which this deliberately never does).
public static class WikidataQueryFailureResult
{
    public static IResult Problem(
        ILogger logger,
        WikidataQueryException ex,
        string logMessageTemplate,
        params object?[] logMessageArgs)
    {
        logger.LogWarning(ex, logMessageTemplate, logMessageArgs);
        return Results.Problem(
            title: "Live verification unavailable",
            detail: "We couldn't reach Wikidata to verify this player. Please try again.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
