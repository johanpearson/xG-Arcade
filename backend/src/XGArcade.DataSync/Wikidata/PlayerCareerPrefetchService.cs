using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// ADR-0055: `dotnet run -- prefetch-player-careers`'s own service — see
// IPlayerCareerPrefetchService's own doc comment for the "what this is for
// / how it differs from PlayerCareerStintRefreshService" summary. A CLI
// verb, not an HTTP endpoint or background task, same ADR-0024 reasoning as
// every other bulk Wikidata job in this codebase (warm-player-cache,
// import-player-name-index, the two backfill services): this can run for a
// long time against a real player pool, far longer than the deployed
// backend's ~240s HTTP ingress allows, and this Container App's
// minReplicas: 0 would silently kill a fire-and-forget background task
// mid-run.
//
// Deliberately scoped to already-SEEDED countries (CategoryValueRepository
// .GetCountriesAsync, not a broader unfiltered pool) — the product owner's
// own explicit choice when this was proposed (ADR-0055's Open questions),
// not a default picked silently. A country with no eligible players simply
// contributes an empty pool, not a failure.
//
// ADR-0069: also sweeps already-SEEDED clubs (CategoryValueRepository
// .GetClubsAsync), via IWikidataClient.QueryPlayerPoolByClubAsync — a
// fresh product decision that extends (not supersedes) ADR-0055's original
// nationality-only scope, so a player from an unseeded country who played
// for a seeded club is no longer invisible to this sweep. Symmetric to the
// country sweep in every way (S-165: both sweeps share the "fetch -> mark
// swept -> skip-empty -> dedup+chunk" shape via SweepCountriesAsync/
// SweepClubsAsync/SweepAsync/SweepPoolAsync below — see their own doc
// comments).
// S-106/S-107 (pure refactor): playerRepository carries
// GetOrCreatePlayersByWikidataQidAsync (split out of the original, now-
// deleted IPlayerStoreRepository); playerCareerStintRepository carries
// GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync — see ADR-0067.
//
// REQ-110 follow-up (2026-08-18): playerAttributeRepository/
// playerDataRepository are new here — both sweeps now ALSO persist a
// PlayerAttribute row (paired with a PlayerData row, same as every other
// Wikidata-derived attribute write in this codebase — REQ-502's admin view
// needs PlayerData's Source/Confidence for every data point) per pooled
// player, not just the Player/PlayerCareerStint rows they always wrote.
// Every player in a pool satisfies that attribute BY CONSTRUCTION of the
// pool query's own WHERE clause — no separate Wikidata read-back needed.
// This is what lets PlayerCacheWarmingService's existing
// CountPlayersWithBothAttributesAsync pre-check
// (backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs) become
// the complete answer for a country/club pair once both sides have been
// swept, eliminating live pairwise SPARQL intersection queries that
// otherwise time out on big-club combinations.
//
// REQ-110/ADR-0078/S-160 (2026-08-18): also stamps
// CountryDefinition/ClubDefinition.PlayerPoolSweptAt = DateTime.UtcNow the
// moment a given country's/club's pool sweep completes successfully (see
// SweepAsync's own comment for exactly when). This is the signal
// PlayerCacheWarmingService now checks (alongside its own
// CountPlayersWithBothAttributesAsync count) to know a pair's local count
// is not just a cache hint but the true, final answer — see
// PlayerCacheWarmingService.WarmAsync's own comment for the read side of
// this and ADR-0078's "For AI agents" section for the invalidation
// contract this write side must never violate.
//
// REQ-110/ADR-0088/S-186 (2026-08-25, Supabase free-tier egress incident):
// SweepAsync now ALSO reads that same PlayerPoolSweptAt signal, not just
// writes it — a country/club whose PlayerPoolSweptAt is already non-null is
// skipped entirely (no fetchPoolAsync call, no markSweptAsync re-write, and
// critically no SweepPoolAsync — meaning no GetPlayerAttributesAsync/
// GetCareerStintsByPlayerIdsAsync dedup read-back either, since those only
// ever run after a pool is actually fetched). Before this, ADR-0078 only
// taught PlayerCacheWarmingService to trust an already-swept pool; this
// service itself had no equivalent shortcut and unconditionally re-swept
// EVERY seeded country and club from scratch on every dispatch, repeating a
// full read-and-write pass against Supabase Postgres regardless of whether
// anything had actually changed since the last successful run. A burst of
// 9 manual re-dispatches in ~36 hours (2026-08-17/18, chasing transient
// WDQS failures) is the confirmed root cause of a ~1.3GB single-day egress
// spike that pushed the org over its free-tier quota. "Ever successfully
// swept" is treated as sufficient — no staleness window — matching this
// data's own volatility (a Wikidata career history rarely changes
// retroactively) and mirroring ADR-0078's own precedent for the sibling
// warm-grid-cache job. Freshness is intentionally forced, not time-based:
// the existing invalidation contract (StaleClubAttributeCleaner/
// purge-player-pool clearing PlayerPoolSweptAt, per ADR-0078's "For AI
// agents" section) is what makes a re-sweep happen again, not a calendar
// window — the same contract this fix inherits unchanged. See ADR-0088 for
// the full decision and alternatives considered.
public class PlayerCareerPrefetchService(
    ICategoryValueRepository categoryValueRepository,
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IPlayerDataRepository playerDataRepository,
    IWikidataClient wikidataClient,
    ILogger<PlayerCareerPrefetchService> logger) : IPlayerCareerPrefetchService
{
    // Quality-gate fix (2026-08-18): reference WikidataLookupService's own
    // constants (made internal for exactly this) instead of redeclaring a
    // second private copy in this class — one definition of the
    // "nationality"/"club" AttributeType spelling, not two kept in sync
    // only by comment discipline. This is the exact string
    // CountPlayersWithBothAttributesAsync later matches against, so there
    // is no room for a second, potentially-divergent spelling.
    private const string NationalityAttributeType = WikidataLookupService.NationalityAttributeType;
    private const string ClubAttributeType = WikidataLookupService.ClubAttributeType;

    // S-189 follow-up (2026-08-29, quality-gate fix): reuses
    // WikidataLookupService's own WikidataSource/VerifiedConfidence (made
    // internal for exactly this) instead of redeclaring a second private
    // copy in this class — every row this service writes to PlayerData is
    // Wikidata-sourced and "verified" by default (ADR-0032: all
    // Wikidata-sourced writes persist verified, no per-origin split needed),
    // same as every other automated Wikidata-derived PlayerAttribute/
    // PlayerData write in this codebase, now with exactly one definition of
    // that pair rather than a copy kept in sync only by comment discipline.
    private const string WikidataDataSource = WikidataLookupService.WikidataSource;
    private const string VerifiedConfidence = WikidataLookupService.VerifiedConfidence;

    // Conservative batch size for QueryPlayerCareerStintsByQidsAsync's VALUES
    // clause within one country's pool — same size PlayerPhotoBackfillService/
    // PlayerPositionBirthYearBackfillService already use, safely inside
    // implementation-document.md §6a's "few-thousand-row, no ORDER BY/LIMIT/
    // OFFSET" bounded-query class.
    public const int CareerBatchSize = 200;

    public async Task<PlayerCareerPrefetchResult> PrefetchAsync(
        int? maxEntitiesToResweep = null, CancellationToken cancellationToken = default)
    {
        var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
        // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203
        // follow-up, ADR-0059): built once for the whole run, not once per
        // batch — ClubDefinition is small (~15 rows, hand-seeded,
        // MVP-SCOPE.md) and doesn't change mid-run, so there's no reason to
        // re-read it inside FetchAndPersistBatchAsync's per-batch loop. See
        // PlayerCareerStintRefreshService.BuildNewStintsByPlayerId's own doc
        // comment for what this map is used for.
        var clubNameByClubQid = await PlayerCareerStintRefreshService.BuildClubNameByClubQidAsync(categoryValueRepository, cancellationToken);

        // S-187: one top-level budget split across the two separate sweep
        // calls below — see SplitResweepBudget's own comment for the split
        // rule.
        var (maxCountriesToResweep, maxClubsToResweep) = SplitResweepBudget(maxEntitiesToResweep);

        var countryOutcome = await SweepCountriesAsync(countries, clubNameByClubQid, maxCountriesToResweep, cancellationToken);

        // ADR-0069: symmetric to the country sweep above (see SweepClubsAsync).
        // Running totals continue from where the country sweep left off.
        var clubOutcome = await SweepClubsAsync(
            clubs, clubNameByClubQid, maxClubsToResweep, countryOutcome.PlayersTouched, countryOutcome.StintsAdded, countryOutcome.AttributesAdded, cancellationToken);

        var careerBatchesFailed = countryOutcome.CareerBatchesFailed + clubOutcome.CareerBatchesFailed;
        if (countryOutcome.Failed > 0 || clubOutcome.Failed > 0 || careerBatchesFailed > 0)
        {
            throw new InvalidOperationException(
                $"prefetch-player-careers: {countryOutcome.Failed} countr{(countryOutcome.Failed == 1 ? "y" : "ies")} " +
                $"failed to fetch their player pool ({string.Join(", ", countryOutcome.FailedNames)}), " +
                $"{clubOutcome.Failed} club(s) failed to fetch their player pool ({string.Join(", ", clubOutcome.FailedNames)}), and " +
                $"{careerBatchesFailed} career-fetch batch(es) failed. {clubOutcome.PlayersTouched} player(s) were still " +
                "touched and " + $"{clubOutcome.StintsAdded} stint(s) added and {clubOutcome.AttributesAdded} attribute(s) added " +
                "from what succeeded; the job is idempotent — re-run it to retry what failed.");
        }

        return new PlayerCareerPrefetchResult(
            countryOutcome.Processed, clubOutcome.PlayersTouched, clubOutcome.StintsAdded, countryOutcome.Failed,
            careerBatchesFailed, clubOutcome.Processed, clubOutcome.Failed, clubOutcome.AttributesAdded,
            countryOutcome.Skipped, clubOutcome.Skipped);
    }

    // S-165: supplies what genuinely differs from the club sweep below
    // (fetch call, mark-swept write, own log wording) to shared SweepAsync.
    // S-187: maxToResweep is this sweep's own share of PrefetchAsync's
    // top-level maxEntitiesToResweep budget — see SplitResweepBudget.
    private Task<SweepOutcome> SweepCountriesAsync(
        IReadOnlyList<CountryDefinition> countries, IReadOnlyDictionary<string, string> clubNameByClubQid,
        int maxToResweep, CancellationToken cancellationToken) =>
        SweepAsync(
            countries, getWikidataQid: c => c.WikidataQid, getName: c => c.Name,
            getSweptAt: c => c.PlayerPoolSweptAt,
            fetchPoolAsync: (c, ct) => wikidataClient.QueryPlayerPoolByNationalityAsync(c.WikidataQid!, c.UsesCountryForSportProperty, ct),
            logFetchFailed: (c, ex) => logger.LogWarning(ex,
                "prefetch-player-careers: {Country} failed; continuing with the remaining countries. " +
                "This run WILL fail at the end, but the job is idempotent — re-run it to fill in the failed countries.",
                c.Name),
            markSweptAsync: (c, ct) => categoryValueRepository.UpdateCountrySweptAtAsync(c.Id, DateTime.UtcNow, ct),
            logSkipped: c => logger.LogDebug(
                "prefetch-player-careers: {Country} skipped — already fully swept (ADR-0088); " +
                "no Wikidata call, no dedup read-back.", c.Name),
            attributeType: NationalityAttributeType, clubNameByClubQid: clubNameByClubQid,
            startingPlayersTouched: 0, startingStintsAdded: 0, startingAttributesAdded: 0,
            logDone: (c, poolSize, touched, added, attrsAdded) => logger.LogInformation(
                "prefetch-player-careers: {Country} done — pool of {PoolSize} player(s) processed " +
                "(running totals: {PlayersTouched} player(s) touched, {StintsAdded} stint(s) added, " +
                "{AttributesAdded} attribute(s) added).",
                c.Name, poolSize, touched, added, attrsAdded), maxToResweep, cancellationToken);

    // Mirrors SweepCountriesAsync above. starting* seeds running totals from
    // the country sweep's own final totals, so they stay cumulative overall.
    // S-187: maxToResweep is this sweep's own share of the budget, see
    // SweepCountriesAsync's own comment.
    private Task<SweepOutcome> SweepClubsAsync(
        IReadOnlyList<ClubDefinition> clubs, IReadOnlyDictionary<string, string> clubNameByClubQid,
        int maxToResweep, int startingPlayersTouched, int startingStintsAdded, int startingAttributesAdded,
        CancellationToken cancellationToken) =>
        SweepAsync(
            clubs, getWikidataQid: c => c.WikidataQid,
            // Quality-gate fix (2026-08-18): deliberately uses club.Name
            // (this exact ClubDefinition row's own name), NOT
            // clubNameByClubQid — the opposite of PlayerCareerStint.ClubName's
            // sourcing inside FetchAndPersistBatchAsync. clubNameByClubQid
            // resolves an ARBITRARY QID off a player's Wikidata career-stint
            // response (any club ever played for, not necessarily this one),
            // where "last club wins on a QID collision" is an accepted
            // approximation (PlayerCareerStintRefreshService
            // .BuildClubNameByClubQidAsync's own comment). Here `c` IS the
            // exact row being swept, so c.Name is unambiguous — going
            // through clubNameByClubQid instead risks mislabeling on a QID
            // collision and would no longer match PlayerCacheWarmingService's
            // own join key (ClubAttributeType/club.Name, itself sourced off
            // ClubDefinition.Name directly). See ADR-0077's correction note.
            // This selector is SweepAsync's attributeValue source.
            getName: c => c.Name,
            getSweptAt: c => c.PlayerPoolSweptAt,
            fetchPoolAsync: (c, ct) => wikidataClient.QueryPlayerPoolByClubAsync(c.WikidataQid!, ct),
            logFetchFailed: (c, ex) => logger.LogWarning(ex,
                "prefetch-player-careers: {Club} failed; continuing with the remaining clubs. " +
                "This run WILL fail at the end, but the job is idempotent — re-run it to fill in the failed clubs.",
                c.Name),
            markSweptAsync: (c, ct) => categoryValueRepository.UpdateClubSweptAtAsync(c.Id, DateTime.UtcNow, ct),
            logSkipped: c => logger.LogDebug(
                "prefetch-player-careers: {Club} skipped — already fully swept (ADR-0088); " +
                "no Wikidata call, no dedup read-back.", c.Name),
            attributeType: ClubAttributeType, clubNameByClubQid: clubNameByClubQid,
            startingPlayersTouched: startingPlayersTouched, startingStintsAdded: startingStintsAdded, startingAttributesAdded: startingAttributesAdded,
            logDone: (c, poolSize, touched, added, attrsAdded) => logger.LogInformation(
                "prefetch-player-careers: {Club} done — pool of {PoolSize} player(s) processed " +
                "(running totals: {PlayersTouched} player(s) touched, {StintsAdded} stint(s) added, " +
                "{AttributesAdded} attribute(s) added).",
                c.Name, poolSize, touched, added, attrsAdded), maxToResweep, cancellationToken);

    private readonly record struct SweepOutcome(
        int Processed, int Failed, IReadOnlyList<string> FailedNames,
        int PlayersTouched, int StintsAdded, int AttributesAdded, int CareerBatchesFailed, int Skipped = 0);

    // S-187 (REQ-110 follow-up, rotating bounded re-sweep): splits one
    // top-level maxEntitiesToResweep budget across SweepCountriesAsync's and
    // SweepClubsAsync's own separate calls into shared SweepAsync — each
    // call only knows its own row set, so the split has to happen here, once,
    // before either runs. The odd remainder rounds toward the country side:
    // there are more seeded countries (49) than clubs (~15, MVP-SCOPE.md), so
    // giving the country half the extra unit keeps each pool's own rotation
    // period (how long until every one of ITS rows has cycled through a
    // re-sweep) roughly comparable, rather than the smaller club budget
    // rounding down twice as often. null or non-positive input collapses to
    // (0, 0) — SweepAsync's existing "0 means no resweep" default, i.e.
    // ADR-0088's unchanged skip-forever behavior.
    private static (int MaxCountriesToResweep, int MaxClubsToResweep) SplitResweepBudget(int? maxEntitiesToResweep)
    {
        if (maxEntitiesToResweep is null or <= 0)
            return (0, 0);

        var countriesShare = (maxEntitiesToResweep.Value + 1) / 2;
        var clubsShare = maxEntitiesToResweep.Value - countriesShare;
        return (countriesShare, clubsShare);
    }

    // S-165: shared "fetch -> mark swept -> skip-empty -> dedup+chunk" shape
    // both sweeps use, extracted from two ~90-line near-identical foreach
    // loops; the delegate params are the genuine per-sweep differences.
    // REQ-109: a null QID is a skip, not a failure. REQ-110/ADR-0078/S-160:
    // markSweptAsync only runs once fetchPoolAsync succeeds — never on the
    // null-QID skip or a caught exception — and an empty pool still counts.
    // REQ-110/ADR-0088/S-186: a row whose getSweptAt is already non-null is
    // skipped BEFORE fetchPoolAsync — no live Wikidata call, no
    // markSweptAsync re-write (the existing timestamp already reflects a
    // genuinely complete sweep — see ADR-0078's "For AI agents" section on
    // when it's allowed to be set), and no SweepPoolAsync (the dedup
    // read-back only ever happens inside SweepPoolAsync, which this skip
    // never reaches). This is a pure re-run-cost fix, not a data-freshness
    // change: getWikidataQid's null-QID skip above is checked first and
    // still takes priority, matching every other precedence in this method.
    //
    // REQ-110/S-187 (rotating bounded re-sweep): maxToResweep widens which
    // already-swept rows are exempted from the skip above. Every never-swept
    // row is still included unconditionally, uncapped by maxToResweep (a
    // backlog of brand-new entities is never throttled) — only the
    // ALREADY-swept population is bounded: up to maxToResweep of them,
    // chosen as the OLDEST getSweptAt values (the ones most overdue for a
    // refresh), get selected up front and treated exactly like a never-swept
    // row for the rest of this method — same fetchPoolAsync call, same
    // markSweptAsync re-write (refreshing the timestamp to "now," restarting
    // its place in the rotation), same SweepPoolAsync dedup read-back.
    // maxToResweep: 0 (SplitResweepBudget's default when the caller passed
    // null) selects nothing here, reproducing ADR-0088's exact skip-forever
    // behavior with no behavior change.
    private async Task<SweepOutcome> SweepAsync<TRow>(
        IReadOnlyList<TRow> rows, Func<TRow, string?> getWikidataQid, Func<TRow, string> getName,
        Func<TRow, DateTime?> getSweptAt,
        Func<TRow, CancellationToken, Task<IReadOnlyList<WikidataNameIndexEntry>>> fetchPoolAsync,
        Action<TRow, WikidataQueryException> logFetchFailed, Func<TRow, CancellationToken, Task> markSweptAsync,
        Action<TRow> logSkipped,
        string attributeType, IReadOnlyDictionary<string, string> clubNameByClubQid,
        int startingPlayersTouched, int startingStintsAdded, int startingAttributesAdded,
        Action<TRow, int, int, int, int> logDone, int maxToResweep, CancellationToken cancellationToken)
    {
        var processed = 0;
        var failed = 0;
        var skipped = 0;
        var failedNames = new List<string>();
        var playersTouched = startingPlayersTouched;
        var stintsAdded = startingStintsAdded;
        var attributesAdded = startingAttributesAdded;
        var careerBatchesFailed = 0;

        // S-187: the rotation's own selection — up to maxToResweep
        // already-swept rows, oldest getSweptAt first. Reference equality
        // (rows are the exact same object instances iterated below) is all
        // this HashSet needs; CountryDefinition/ClubDefinition define no
        // value-equality override.
        //
        // getWikidataQid(r) is not null (defensive hardening, 2026-08-29,
        // quality-architect finding): can't currently manifest as a bug — a
        // swept row can't have a null QID given how ReferenceDataSeeder
        // seeds CountryDefinition/ClubDefinition — but keeps this selection
        // aligned with the main loop's own null-QID skip below regardless.
        var selectedForResweep = maxToResweep > 0
            ? rows.Where(r => getSweptAt(r) is not null && getWikidataQid(r) is not null)
                .OrderBy(getSweptAt)
                .Take(maxToResweep)
                .ToHashSet()
            : [];

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (getWikidataQid(row) is null)
                continue;

            if (getSweptAt(row) is not null && !selectedForResweep.Contains(row))
            {
                skipped++;
                logSkipped(row);
                continue;
            }

            IReadOnlyList<WikidataNameIndexEntry> pool;
            try
            {
                pool = await fetchPoolAsync(row, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                failed++;
                failedNames.Add(getName(row));
                logFetchFailed(row, ex);
                continue;
            }

            processed++;
            await markSweptAsync(row, cancellationToken);
            if (pool.Count == 0)
                continue;

            var (touched, added, attrsAdded, batchesFailed) = await SweepPoolAsync(
                pool, clubNameByClubQid, attributeType, getName(row), cancellationToken);
            playersTouched += touched;
            stintsAdded += added;
            attributesAdded += attrsAdded;
            careerBatchesFailed += batchesFailed;

            logDone(row, pool.Count, playersTouched, stintsAdded, attributesAdded);
        }

        return new SweepOutcome(processed, failed, failedNames, playersTouched, stintsAdded, attributesAdded, careerBatchesFailed, skipped);
    }

    // S-165: byte-identical tail both sweeps share once a pool is fetched and
    // non-empty. attributeValue comes from the caller (getName), never
    // derived here — preserves the club.Name-vs-clubNameByClubQid distinction.
    private async Task<(int PlayersTouched, int StintsAdded, int AttributesAdded, int BatchesFailed)> SweepPoolAsync(
        IReadOnlyList<WikidataNameIndexEntry> pool, IReadOnlyDictionary<string, string> clubNameByClubQid,
        string attributeType, string attributeValue, CancellationToken cancellationToken)
    {
        // REQ-110 follow-up: fetched once per pool (not per batch/player) —
        // every player in `pool` satisfies this attribute by construction of
        // the pool query's own WHERE clause, so the only remaining question
        // is dedup against what's already stored. Same "fetch once,
        // HashSet.Add as the dedup gate" pattern as WikidataLookupService.
        // PersistMatchesAsync's playerIdsWithAttributeA/B.
        var playerIdsWithAttribute = (await playerAttributeRepository.GetPlayerAttributesAsync(
                attributeType, attributeValue, cancellationToken))
            .Select(a => a.PlayerId)
            .ToHashSet();

        var playersTouched = 0;
        var stintsAdded = 0;
        var attributesAdded = 0;
        var batchesFailed = 0;
        foreach (var batch in pool.Chunk(CareerBatchSize))
        {
            var (touched, added, attrsAdded, batchFailed) = await FetchAndPersistBatchAsync(
                batch, clubNameByClubQid, attributeType, attributeValue, playerIdsWithAttribute, cancellationToken);
            playersTouched += touched;
            stintsAdded += added;
            attributesAdded += attrsAdded;
            if (batchFailed)
                batchesFailed++;
        }

        return (playersTouched, stintsAdded, attributesAdded, batchesFailed);
    }

    // Returns whether the career-fetch step itself failed (distinct from
    // "fetched but found nothing," a normal, non-failure outcome) so the
    // caller can keep a separate failure tally without this method needing
    // to throw and unwind the whole pool's remaining batches over one
    // batch's failure.
    //
    // REQ-110 follow-up: attributeType/attributeValue/playerIdsWithAttribute
    // describe THIS pool's own attribute (nationality+country.Name or
    // club+club.Name — see SweepClubsAsync's own comment on why club.Name,
    // not clubNameByClubQid, is correct here) — every player in `batch` gets
    // it queued, deduped against playerIdsWithAttribute, which the caller
    // built once per pool (not per batch) and passes by reference so dedup
    // state accumulates across a pool's CareerBatchSize-sized batches.
    private async Task<(int PlayersTouched, int StintsAdded, int AttributesAdded, bool BatchFailed)> FetchAndPersistBatchAsync(
        IReadOnlyList<WikidataNameIndexEntry> batch,
        IReadOnlyDictionary<string, string> clubNameByClubQid,
        string attributeType,
        string attributeValue,
        HashSet<Guid> playerIdsWithAttribute,
        CancellationToken cancellationToken)
    {
        // REQ-214/REQ-1207's existing "set only at creation, never
        // overwritten on a later lookup" contract applies here unchanged —
        // a player already known from an earlier xG Grid lookup keeps
        // whatever PhotoUrl/Position they already have; PlayerNameIndex has
        // no PhotoUrl/Position of its own to offer regardless (dropped
        // 2026-07-18 / never added — see PlayerNameIndex.cs's own comment).
        var requests = batch
            .Select(entry => new PlayerCreationRequest(entry.WikidataQid, entry.FullName, PhotoUrl: null, Position: null, entry.BirthYear))
            .ToList();
        var playersByQid = await playerRepository.GetOrCreatePlayersByWikidataQidAsync(requests, cancellationToken);

        // REQ-110 follow-up: every player just fetched/created for this
        // batch satisfies attributeType/attributeValue by construction of
        // the pool query that produced `batch` — queue+persist that fact
        // now, deduped against playerIdsWithAttribute (shared across this
        // pool's whole set of batches), regardless of whether the
        // career-fetch step below succeeds or fails. This intentionally
        // runs before the try/catch so a career-fetch batch failure never
        // costs the (unrelated, purely local) attribute write.
        //
        // Quality-gate fix (2026-08-18): pairs each new PlayerAttribute row
        // with a PlayerData row, required so REQ-502's admin view has a
        // Source/Confidence to show for these rows — but deliberately NOT
        // the same shape as WikidataLookupService.QueueAttribute, whose own
        // comment calls PlayerData "a raw, per-source append log ... always
        // recorded" regardless of whether the paired PlayerAttribute is new.
        // Here BOTH lists are gated behind the identical
        // playerIdsWithAttribute.Add(...) dedup check: a repeat sweep that
        // re-confirms an already-known nationality/club fact does not
        // re-append a fresh PlayerData row for it. That's the right call for
        // this bulk sweep specifically — the fact being recorded doesn't
        // change run to run the way a fresh per-guess Wikidata match can,
        // and appending an unchanged PlayerData row on every one of this
        // job's ~weekly full-pool sweeps would grow that table for zero
        // informational gain. One shared syncedAt per batch call (not a
        // fresh timestamp per player), same as PersistMatchesAsync.
        var attributesToAdd = new List<PlayerAttribute>();
        var playerDataToAdd = new List<PlayerData>();
        var syncedAt = DateTime.UtcNow;
        foreach (var player in playersByQid.Values.Select(r => r.Player))
        {
            if (!playerIdsWithAttribute.Add(player.Id))
                continue;

            attributesToAdd.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = attributeType, AttributeValue = attributeValue });
            playerDataToAdd.Add(new PlayerData
            {
                Id = Guid.NewGuid(),
                PlayerId = player.Id,
                Field = attributeType,
                Value = attributeValue,
                Source = WikidataDataSource,
                Confidence = VerifiedConfidence,
                SyncedAt = syncedAt,
            });
        }
        if (attributesToAdd.Count > 0)
        {
            await playerAttributeRepository.AddPlayerAttributesBatchAsync(attributesToAdd, cancellationToken);
            await playerDataRepository.AddPlayerDataBatchAsync(playerDataToAdd, cancellationToken);
        }

        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid;
        try
        {
            stintsByQid = await wikidataClient.QueryPlayerCareerStintsByQidsAsync(playersByQid.Keys.ToList(), cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            logger.LogWarning(ex,
                "prefetch-player-careers: a career-fetch batch of {BatchSize} player(s) failed; " +
                "skipping to the next batch.", playersByQid.Count);
            return (playersByQid.Count, 0, attributesToAdd.Count, true);
        }

        if (stintsByQid.Count == 0)
            return (playersByQid.Count, 0, attributesToAdd.Count, false);

        var qidToPlayerId = playersByQid.ToDictionary(kv => kv.Key, kv => kv.Value.Player.Id);
        var affectedPlayerIds = stintsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();
        var existingStintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(affectedPlayerIds, cancellationToken);

        var reconciliation = PlayerCareerStintRefreshService.BuildNewStintsByPlayerId(
            stintsByQid, qidToPlayerId, existingStintsByPlayerId, clubNameByClubQid);

        if (reconciliation.NewStintsByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(reconciliation.NewStintsByPlayerId, cancellationToken);

        // S-187 (REQ-1203): completions (an already-stored stint's
        // previously-null EndYear/AppearanceCount now filled in) are a
        // separate write from new-row inserts above — see
        // BuildNewStintsByPlayerId's own doc comment for the full "why."
        if (reconciliation.CompletionsByStintId.Count > 0)
            await playerCareerStintRepository.UpdateCareerStintCompletionsAsync(reconciliation.CompletionsByStintId, cancellationToken);

        return (playersByQid.Count, reconciliation.NewStintsByPlayerId.Sum(kv => kv.Value.Count), attributesToAdd.Count, false);
    }
}
