using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace XGArcade.Core.IncidentReporting;

// REQ-904/ADR-0066: the TTL is a typed DI parameter for the same reason
// GitHubIncidentReportToken/GitHubIncidentReportOptions are (COMP-12's own
// established convention) — a bare `TimeSpan` can't be registered/resolved
// unambiguously alongside every other service this project registers.
// Default (60s) lives here so it's discoverable next to the type it
// configures, same as GitHubIncidentReportOptions's Program.cs defaults
// being the single source of truth for "what happens if configuration is
// absent."
public record IncidentReportCacheTtl(TimeSpan Value)
{
    public static readonly TimeSpan DefaultValue = TimeSpan.FromSeconds(60);
}

// COMP-12/REQ-904: the only caller GET /admin/incident-reports is allowed
// to use (ADR-0066) — never IGitHubIssueClient directly. Registered as a
// singleton (Program.cs) so its cache state and "last successful poll" are
// genuinely shared across every admin request, not per-request/per-scope.
public interface ICachedIncidentIssueSummaryProvider
{
    Task<IncidentIssueSummaryResult> GetAsync(CancellationToken cancellationToken);
}

// Available=false means "there has never been a successful poll" (ADR-0066:
// cold start during an outage, or the token has never been configured) —
// the one case this provider refuses to fabricate a result for. Issues is
// non-null exactly when Available is true.
public record IncidentIssueSummaryResult(bool Available, IReadOnlyList<GitHubIssueSummary>? Issues)
{
    public static IncidentIssueSummaryResult Ok(IReadOnlyList<GitHubIssueSummary> issues) => new(true, issues);

    public static readonly IncidentIssueSummaryResult Unavailable = new(false, null);
}

// ADR-0066's caching decision, precisely: a single shared cache entry
// (never per-admin/per-request) with a short absolute TTL, and — the part
// that makes this more than a generic cache-aside wrapper — a GitHub
// failure never immediately flips a working admin UI to an error state.
// The last successfully-polled result is kept independently of the
// IMemoryCache entry's own TTL (IMemoryCache evicts an expired entry
// outright, so it can't itself serve "stale but still know it") and is
// re-served on failure; only a GitHub failure with no prior success ever
// falls through to Unavailable.
public class CachedIncidentIssueSummaryProvider(
    IGitHubIssueClient gitHubIssueClient,
    IMemoryCache memoryCache,
    IncidentReportCacheTtl cacheTtl,
    ILogger<CachedIncidentIssueSummaryProvider> logger) : ICachedIncidentIssueSummaryProvider
{
    // Single fixed key — ADR-0066 is explicit this cache is not
    // per-admin/per-request; every admin within the TTL window reads the
    // same entry.
    private const string CacheKey = "incident-reports:open-issues";

    // Deliberately NOT another IMemoryCache entry: an IMemoryCache entry is
    // evicted outright once its TTL expires, which is exactly the state
    // ADR-0066 says must still be servable as a fallback ("even if its TTL
    // has technically expired"). A plain instance field on this singleton
    // is the simplest way to keep "last known good" alive independently of
    // the TTL'd cache above. Read/write races here are benign (worst case,
    // a concurrent request reads a value one write behind) — no lock is
    // used, matching this feature's low-traffic, admin-only call pattern.
    private IReadOnlyList<GitHubIssueSummary>? _lastSuccessfulIssues;

    public async Task<IncidentIssueSummaryResult> GetAsync(CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue(CacheKey, out IReadOnlyList<GitHubIssueSummary>? cached) && cached is not null)
            return IncidentIssueSummaryResult.Ok(cached);

        var listResult = await gitHubIssueClient.ListOpenIssuesByLabelAsync(cancellationToken);
        if (listResult.Success && listResult.Issues is not null)
        {
            memoryCache.Set(CacheKey, listResult.Issues, cacheTtl.Value);
            _lastSuccessfulIssues = listResult.Issues;
            return IncidentIssueSummaryResult.Ok(listResult.Issues);
        }

        // ADR-0066: a GitHub failure serves the last successfully-cached
        // result if one exists, even past its TTL — a transient outage
        // should not immediately flip a working admin UI to an error
        // state. Only when there has never been a successful poll (cold
        // start during an outage, or an unconfigured token) is the
        // explicit failure returned.
        if (_lastSuccessfulIssues is not null)
        {
            logger.LogWarning(
                "Incident report poll failed ({FailureReason}); serving the last successfully-cached result.",
                listResult.FailureReason);
            return IncidentIssueSummaryResult.Ok(_lastSuccessfulIssues);
        }

        logger.LogWarning(
            "Incident report poll failed ({FailureReason}) with no prior successful poll to fall back on.",
            listResult.FailureReason);
        return IncidentIssueSummaryResult.Unavailable;
    }
}
