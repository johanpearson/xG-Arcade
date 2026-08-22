using System.Text.Json.Serialization;

namespace XGArcade.DataSync.Wikidata;

// S-155 (docs/backlog.md, Epic 17): every SPARQL response-parsing method
// WikidataClient's public IWikidataClient methods (via the RunIntersectionQueryAsync/
// RunThrowingQueryAsync drivers) call into, plus the raw SparqlResponse/
// SparqlResults/SparqlValue JSON-deserialization shapes those parsers (and
// the two drivers themselves) read — moved out of WikidataClient.cs
// unchanged (only the containing class changed; see
// WikidataClientTests.cs's existing assertions on parsed results for the
// regression proof this didn't change parsing behavior). See
// SparqlQueryBuilders.cs's own doc comment for the full
// CODE_HEALTH_ASSESSMENT.md/S-155 background — this is the parsing half of
// that same split. internal, not private, for the same reason as that
// file: WikidataClient.cs's Run* drivers (SparqlResponse, to deserialize
// into) and thin wrapper methods (each Parse* delegate) reach these from a
// different class in the same namespace/assembly; nothing outside
// XGArcade.DataSync needs them. MergeCareerStintEntries/NormalizeClubName/
// ClubNameLegalSuffixes/TryParseXsdDateTimeYear stay private — used only
// internally by the Parse* methods in this same class, never called from
// WikidataClient.cs directly.
internal static class SparqlResponseParsers
{
    // Bug fix (2026-08-03, user-tester report): a real report showed the
    // autocomplete suggestion for Michael Owen (the footballer, actually
    // born 1979) carrying BirthYear 1976. wdt:P569 is a truthy predicate —
    // it already collapses to a single preferred-rank statement whenever
    // Wikidata has one, so this can only happen when an item genuinely
    // carries more than one non-deprecated P569 statement with NEITHER
    // marked preferred (a real, if uncommon, state of Wikidata's own data —
    // e.g. an old/erroneous secondary-sourced date nobody has cleaned up).
    // QueryPlayerPoolByNationalityAsync's query has no per-year window, so
    // both statements land as separate rows for the same ?player in ONE
    // response; before this fix, whichever row happened to come first in
    // WDQS's own (unspecified, engine-internal) result order silently won,
    // with no correctness signal behind that choice at all. See this
    // method's own handling below for the fix.
    internal static IReadOnlyList<WikidataNameIndexEntry> ParseNameIndexBindings(SparqlResponse? response)
    {
        if (response?.Results?.Bindings is null)
            return [];

        var byQid = new Dictionary<string, (string FullName, int? BirthYear, string? Nationality)>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();

            int? rowBirthYear = binding.TryGetValue("birthYear", out var birthYearValue)
                && int.TryParse(birthYearValue.Value, out var parsedBirthYear)
                    ? parsedBirthYear
                    : null;

            if (!byQid.TryGetValue(qid, out var entry))
            {
                var label = binding.TryGetValue("playerLabel", out var labelValue) ? labelValue.Value : qid;
                entry = (label, rowBirthYear, null);
            }
            else if (entry.BirthYear is not null && rowBirthYear is not null && entry.BirthYear != rowBirthYear)
            {
                // Two rows for the same player disagree on birth year — a
                // genuine ambiguity this query has no way to resolve (see
                // this method's own doc comment above). Rather than keeping
                // whichever value happened to arrive first — an artifact of
                // WDQS's own internal row ordering, not a correctness signal
                // — the birth year is nulled out. Same "omit rather than
                // mislead" convention this codebase already applies
                // elsewhere (e.g. an unknown club appearance count is
                // omitted, never shown as a misleading "0 apps" —
                // PathClubClue's own doc comment). The player's name still
                // surfaces in autocomplete either way; only the (never
                // load-bearing, REQ-207) birth-year hint is dropped.
                entry.BirthYear = null;
            }

            // A player with more than one citizenship produces more than one
            // binding row — keep the first non-null value seen, rather than
            // overwriting with a later (possibly blank) one.
            if (entry.Nationality is null && binding.TryGetValue("countryLabel", out var countryValue)
                && !string.IsNullOrWhiteSpace(countryValue.Value))
                entry.Nationality = countryValue.Value;

            byQid[qid] = entry;
        }

        return byQid
            .Select(kv => new WikidataNameIndexEntry(kv.Key, kv.Value.FullName, kv.Value.BirthYear, kv.Value.Nationality))
            .ToList();
    }

    // Keyed by QID (not grouped/deduped the way ParseBindings's byQid
    // dictionary is) — VALUES + OPTIONAL yields exactly one row per QID in
    // the batch regardless of match, so there is no multi-row-per-player
    // grouping concern here the way there is for the intersection queries'
    // alias fetch. Still takes the first non-null value seen per QID (same
    // defensive shape as ParseBindings/ParseNameIndexBindings) in case a
    // player somehow has more than one P18 statement. A QID with no "photo"
    // binding (no P18 statement) is simply absent from the result — never
    // an error, never a placeholder entry.
    internal static IReadOnlyDictionary<string, string> ParsePhotoBindings(SparqlResponse? response)
    {
        var photoUrlsByQid = new Dictionary<string, string>();
        if (response?.Results?.Bindings is null)
            return photoUrlsByQid;

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;
            if (!binding.TryGetValue("photo", out var photoValue) || string.IsNullOrWhiteSpace(photoValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();
            photoUrlsByQid.TryAdd(qid, photoValue.Value);
        }

        return photoUrlsByQid;
    }

    // Grouped by QID (unlike ParsePhotoBindings' plain "one row per QID"
    // shape) because a player with more than one P413 statement, or more
    // than one P569 statement, can legitimately produce more than one
    // binding row per QID — same "keep the first non-null value seen per
    // field" defensive shape ParseBindings already uses for
    // WikidataPlayerMatch.Position/BirthYear. Only entries where at least
    // one of Position/BirthYear actually resolved are included in the
    // result — a QID with neither is simply absent, never an error, same
    // contract as ParsePhotoBindings.
    internal static IReadOnlyDictionary<string, PlayerPositionBirthYearEntry> ParsePositionBirthYearBindings(SparqlResponse? response)
    {
        var entriesByQid = new Dictionary<string, (string? Position, int? BirthYear)>();
        if (response?.Results?.Bindings is null)
            return new Dictionary<string, PlayerPositionBirthYearEntry>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();
            (string? Position, int? BirthYear) entry = entriesByQid.TryGetValue(qid, out var existing) ? existing : default;

            // Reads "positionLabel" (bug fix, 2026-08-02) — see
            // BuildPlayerPositionsAndBirthYearsByQidsQuery's own comment.
            if (entry.Position is null && binding.TryGetValue("positionLabel", out var positionValue)
                && !string.IsNullOrWhiteSpace(positionValue.Value))
                entry.Position = positionValue.Value;

            if (entry.BirthYear is null && binding.TryGetValue("dateOfBirth", out var dateOfBirthValue)
                && !string.IsNullOrWhiteSpace(dateOfBirthValue.Value)
                && TryParseXsdDateTimeYear(dateOfBirthValue.Value, out var birthYear))
                entry.BirthYear = birthYear;

            entriesByQid[qid] = entry;
        }

        return entriesByQid
            .Where(kv => kv.Value.Position is not null || kv.Value.BirthYear is not null)
            .ToDictionary(kv => kv.Key, kv => new PlayerPositionBirthYearEntry(kv.Value.Position, kv.Value.BirthYear));
    }

    // Grouped by QID, one list entry per distinct (ClubName, StartYear,
    // EndYear, AppearanceCount) tuple — same HashSet-based dedup
    // ParseBindings' CareerStints field uses, for the same reason (SPARQL's
    // OPTIONAL semantics can otherwise multiply rows). A row where
    // startTime never bound carries zero information (StartYear is
    // non-nullable on WikidataCareerStintEntry) and is skipped, same as
    // ParseBindings' own CareerStintQualifiers construction. A row with no
    // clubLabel binding at all (should not happen — ?club is a mandatory,
    // non-OPTIONAL match) is also skipped defensively rather than persisting
    // a stint with a blank club name.
    //
    // Bug fix (2026-08-03, xG Path duplicate-stint bug, REQ-1203): the
    // ClubName the HashSet dedups (and every caller ultimately persists) is
    // ?clubLabel, since BuildPlayerCareerStintsByQidsQuery never selects the
    // underlying ?club QID itself — this method has no QID to dedupe or key
    // on, only the rendered label string. Observed in production (bug
    // report with screenshot): one real stint surfaced across two rows as
    // "Liverpool" on one and "Liverpool F.C." on the other — otherwise
    // identical (start, end, appearance count), so the two rows are
    // structurally the same real stint but fail this HashSet's exact
    // string/record equality and show up as two path nodes. WHY Wikidata's
    // own statements carry two label variants for what is presumably one
    // underlying ?club (or two ?club items both resolving to "the same"
    // real club) isn't diagnosable from this sandbox without a live SPARQL
    // query against wikidata.org — see NormalizeClubName's own comment for
    // the (deliberately narrow) fix applied to the observed symptom.
    //
    // Formerly-ACCEPTED limitation of the above fix (quality-gate finding,
    // 2026-08-03; partially fixed 2026-08-10, bug-bundle): dedup used to be
    // keyed on the FULL (ClubName, StartYear, EndYear, AppearanceCount)
    // tuple via a plain HashSet, so normalizing ClubName alone only
    // collapsed duplicate rows that also agreed on every other field. Two
    // rows for what is really the same stint but that disagree on
    // AppearanceCount (e.g. one row's P1350 qualifier absent -> null, the
    // other's present -> 25 -- plausible, since two Wikidata statements for
    // "the same" stint can carry differently-complete qualifiers) failed to
    // merge and reproduced the duplicate-node symptom for that variant —
    // this is exactly the "AC Milan 25 apps" / "AC Milan 95 apps" and bare
    // "Real Sociedad" / "Real Sociedad 2 apps" shapes from the 2026-08-10
    // bug report.
    //
    // MergeCareerStintEntries below now handles the NULL-vs-populated case:
    // a null AppearanceCount means "unknown," and a populated value seen on
    // another row for the same (ClubName, StartYear, EndYear) is strictly
    // more informative, not a conflict, so those two rows merge into one,
    // keeping the populated count. The genuinely dangerous case — BOTH rows
    // populated but with DIFFERENT AppearanceCount values — is still
    // deliberately left unmerged: treating that as a match would risk
    // silently merging two GENUINELY different stints at the same club with
    // matching dates but different, both-known appearance counts (e.g. a
    // loan-and-return spell recorded as two separate P54 statements) — a
    // correctness risk, not just a display one, and a strictly worse
    // failure mode than the display duplicate this fix targets. See
    // REQ1203_QueryPlayerCareerStintsByQidsAsync_DoesNotMergeSameClubAndDates_WhenBothAppearanceCountsPopulatedButDiffer
    // for the test locking this narrower carve-out in place.
    internal static IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> ParseCareerStintBindings(SparqlResponse? response)
    {
        var rawEntriesByQid = new Dictionary<string, List<WikidataCareerStintEntry>>();
        if (response?.Results?.Bindings is null)
            return new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            if (!binding.TryGetValue("clubLabel", out var clubLabelValue) || string.IsNullOrWhiteSpace(clubLabelValue.Value))
                continue;

            if (!binding.TryGetValue("startTime", out var startTimeValue) || !TryParseXsdDateTimeYear(startTimeValue.Value, out var startYear))
                continue;

            int? endYear = binding.TryGetValue("endTime", out var endTimeValue)
                && TryParseXsdDateTimeYear(endTimeValue.Value, out var parsedEndYear)
                    ? parsedEndYear
                    : null;
            int? appearanceCount = binding.TryGetValue("numberOfMatches", out var numberOfMatchesValue)
                && int.TryParse(numberOfMatchesValue.Value, out var parsedAppearanceCount)
                    ? parsedAppearanceCount
                    : null;

            var qid = playerValue.Value.Split('/').Last();
            if (!rawEntriesByQid.TryGetValue(qid, out var entries))
                rawEntriesByQid[qid] = entries = [];

            // ?club (bug fix, 2026-08-04, REQ-1203 follow-up): same
            // "trailing URI segment is the QID" extraction as ?player above.
            // Defensively tolerated as absent (null), even though ?club is a
            // mandatory, non-OPTIONAL match in the query body — a test
            // fixture or an unexpected WDQS response shape omitting it must
            // not drop an otherwise-usable row; the caller-side
            // canonicalization step (PlayerCareerStintRefreshService/
            // PlayerCareerPrefetchService) simply falls back to the
            // normalized label when ClubQid is null, same as it does for a
            // QID that doesn't match any seeded ClubDefinition.
            var clubQid = binding.TryGetValue("club", out var clubValue) && !string.IsNullOrEmpty(clubValue.Value)
                ? clubValue.Value.Split('/').Last()
                : null;

            // Normalize BEFORE MergeCareerStintEntries sees it: this is the
            // club name that both merging and every downstream persistence
            // use — see WikidataCareerStintEntry's own doc comment and this
            // class's NormalizeClubName for why the canonical (not raw)
            // form is what gets stored. NormalizeClubName's suffix-strip is
            // still applied here as the best-effort fallback label (used
            // when ClubQid doesn't resolve to a seeded ClubDefinition) —
            // QID-based canonicalization happens one layer up, not in this
            // client, per this class's own "no ClubDefinition dependency"
            // layering convention within COMP-07 (not a documented
            // cross-component boundary rule — see architecture-document.md's
            // numbered boundary list, which has no entry for this).
            entries.Add(new WikidataCareerStintEntry(NormalizeClubName(clubLabelValue.Value), startYear, endYear, appearanceCount, clubQid));
        }

        return rawEntriesByQid.ToDictionary(kv => kv.Key, kv => MergeCareerStintEntries(kv.Value));
    }

    // Bug fix (2026-08-10, bug-bundle): replaces the plain HashSet-based
    // exact-tuple dedup ParseCareerStintBindings used to do directly. Groups
    // a single player's raw parsed entries by (ClubName, StartYear,
    // EndYear) — the same-real-stint identity — and, within each group,
    // applies the deliberate merge rule described in
    // ParseCareerStintBindings' own comment above:
    //   - exactly one distinct POPULATED AppearanceCount present (whether
    //     alongside one or more null-AppearanceCount rows, or alone): merge
    //     down to a single entry carrying that populated count. A null
    //     AppearanceCount elsewhere in the group is informationally
    //     subsumed, never a conflict.
    //   - more than one distinct POPULATED AppearanceCount present: leave
    //     every row as its own entry, unmerged — the correctness-risk case,
    //     a deliberate non-fix, not an oversight.
    //   - no populated AppearanceCount in the group at all (every row
    //     null): nothing to merge; exact structural duplicates still
    //     collapse via Distinct(), same as the old HashSet did for the
    //     whole record.
    private static IReadOnlyList<WikidataCareerStintEntry> MergeCareerStintEntries(List<WikidataCareerStintEntry> entries)
    {
        var merged = new List<WikidataCareerStintEntry>();

        foreach (var group in entries.GroupBy(e => (e.ClubName, e.StartYear, e.EndYear)))
        {
            var rows = group.ToList();
            var distinctPopulatedCounts = rows
                .Where(r => r.AppearanceCount is not null)
                .Select(r => r.AppearanceCount!.Value)
                .Distinct()
                .ToList();

            if (distinctPopulatedCounts.Count == 1)
            {
                var populatedCount = distinctPopulatedCounts[0];
                var clubQid = rows.Select(r => r.ClubQid).FirstOrDefault(qid => qid is not null);
                merged.Add(rows[0] with { AppearanceCount = populatedCount, ClubQid = clubQid });
                continue;
            }

            // Either >1 distinct populated counts (correctness-risk case,
            // left unmerged) or 0 (nothing to merge) — either way, keep
            // every row, only collapsing exact structural duplicates.
            merged.AddRange(rows.Distinct());
        }

        return merged;
    }

    // Legal-suffix variants Wikidata is observed to use interchangeably for
    // what is the same real club (e.g. "Liverpool" vs "Liverpool F.C.",
    // both attested as ?clubLabel values for the same P54 statement shape —
    // see ParseCareerStintBindings' own comment for the exact bug this
    // fixes). Ordered longest-first so a longer variant (e.g. "A.F.C.")
    // is matched whole rather than partially matching a shorter entry
    // later in the list ("F.C.") first.
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

    private static string NormalizeClubName(string rawClubName)
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

    // Keyed by QID, same "one row per batch entry, absent means none" shape
    // as ParsePhotoBindings — a QID whose sitelink count didn't parse as an
    // integer is treated as absent (never a 0 masquerading as "resolved but
    // unfamiliar"), so XGPathGameModule's threshold check correctly treats
    // it the same as "no data available" rather than "confirmed obscure."
    internal static IReadOnlyDictionary<string, int> ParseSitelinkCountBindings(SparqlResponse? response)
    {
        var sitelinkCountsByQid = new Dictionary<string, int>();
        if (response?.Results?.Bindings is null)
            return sitelinkCountsByQid;

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;
            if (!binding.TryGetValue("sitelinks", out var sitelinksValue)
                || !int.TryParse(sitelinksValue.Value, out var count))
                continue;

            var qid = playerValue.Value.Split('/').Last();
            sitelinkCountsByQid.TryAdd(qid, count);
        }

        return sitelinkCountsByQid;
    }

    // Single-row result (LIMIT 1) — takes the first (only) binding, same
    // "absent means none, never an error" contract as ParsePhotoBindings.
    // A binding with no playerLabel is defensively treated as no match at
    // all (never seen in practice, since SERVICE wikibase:label always
    // resolves a label for any real player item, but this avoids ever
    // returning a WikidataPlayerPhotoLookupResult with an empty FullName).
    internal static WikidataPlayerPhotoLookupResult? ParsePlayerPhotoByNameBinding(SparqlResponse? response)
    {
        var binding = response?.Results?.Bindings?.FirstOrDefault();
        if (binding is null)
            return null;
        if (!binding.TryGetValue("playerLabel", out var labelValue) || string.IsNullOrWhiteSpace(labelValue.Value))
            return null;

        var photoUrl = binding.TryGetValue("photo", out var photoValue) && !string.IsNullOrWhiteSpace(photoValue.Value)
            ? photoValue.Value
            : null;

        return new WikidataPlayerPhotoLookupResult(labelValue.Value, photoUrl);
    }

    // Grouped across every row this query can produce (unlike
    // ParsePlayerPhotoByNameBinding's single LIMIT-1 row): the name-match
    // subquery bounds this to exactly one candidate ?player, but that
    // player's own OPTIONAL P54 club rows still multiply rows the same way
    // ParseCareerStintBindings' own comment describes. WikidataQid/FullName/
    // Nationality are read from the first row that binds each (they're
    // constant across every row for a single matched player); Clubs is a
    // HashSet<string> of distinct club-name labels — simpler than
    // ParseCareerStintBindings' HashSet-of-tuples dedup since this method's
    // Clubs is a plain name list (see WikidataPlayerCareerLookupResult's own
    // doc comment for why), so any two rows sharing a ?clubLabel are simply
    // the same club regardless of what else differs between the rows.
    // Returns null only when no row at all was returned — a genuine "no
    // footballer matches this name," never a swallowed failure (see this
    // method's own doc comment on IWikidataClient for the full
    // error-contract reasoning).
    internal static WikidataPlayerCareerLookupResult? ParsePlayerCareerAndNationalityByNameBindings(SparqlResponse? response)
    {
        var bindings = response?.Results?.Bindings;
        if (bindings is null || bindings.Count == 0)
            return null;

        string? wikidataQid = null;
        string? fullName = null;
        string? nationality = null;
        var clubNames = new HashSet<string>();

        foreach (var binding in bindings)
        {
            if (wikidataQid is null && binding.TryGetValue("player", out var playerValue) && !string.IsNullOrEmpty(playerValue.Value))
                wikidataQid = playerValue.Value.Split('/').Last();

            if (fullName is null && binding.TryGetValue("playerLabel", out var labelValue) && !string.IsNullOrWhiteSpace(labelValue.Value))
                fullName = labelValue.Value;

            if (nationality is null && binding.TryGetValue("nationalityLabel", out var nationalityValue) && !string.IsNullOrWhiteSpace(nationalityValue.Value))
                nationality = nationalityValue.Value;

            // Bug fix (2026-08-08, REQ-509/510): a club is recorded whenever
            // ?clubLabel is bound AT ALL — deliberately NOT gated on
            // ?startTime also being bound (the original bug: not every real
            // P54 statement carries a P580 start-time qualifier, and gating
            // on it silently dropped those clubs — see
            // WikidataPlayerCareerLookupResult's own doc comment for the
            // full "why"). ?startTime/?endTime/?numberOfMatches are still
            // OPTIONAL-fetched by the query for parity with
            // QueryPlayerCareerStintsByQidsAsync's shape, but this method's
            // Clubs never needed them (only ClubName is ever read by
            // AdminSuggestionEndpoints/CommitPlayerDataRequest.Clubs), so
            // they're intentionally left unparsed here.
            if (binding.TryGetValue("clubLabel", out var clubLabelValue) && !string.IsNullOrWhiteSpace(clubLabelValue.Value))
                clubNames.Add(clubLabelValue.Value);
        }

        // wikidataQid/fullName absent means the name-match subquery itself
        // never bound ?player — no footballer matches this name at all.
        if (wikidataQid is null || fullName is null)
            return null;

        return new WikidataPlayerCareerLookupResult(wikidataQid, fullName, nationality, clubNames.ToList());
    }

    internal static IReadOnlyList<WikidataPlayerMatch> ParseBindings(SparqlResponse? response)
    {
        if (response?.Results?.Bindings is null)
            return [];

        var byQid = new Dictionary<string, (string FullName, HashSet<string> Aliases, string? PhotoUrl, string? Position, int? BirthYear, HashSet<CareerStintQualifiers> CareerStints)>();

        foreach (var binding in response.Results.Bindings)
        {
            if (!binding.TryGetValue("player", out var playerValue) || string.IsNullOrEmpty(playerValue.Value))
                continue;

            var qid = playerValue.Value.Split('/').Last();

            if (!byQid.TryGetValue(qid, out var entry))
            {
                var label = binding.TryGetValue("playerLabel", out var labelValue) ? labelValue.Value : qid;
                entry = (label, [], null, null, null, []);
            }

            if (binding.TryGetValue("alias", out var aliasValue) && !string.IsNullOrWhiteSpace(aliasValue.Value))
                entry.Aliases.Add(aliasValue.Value);

            // REQ-214: one row can carry the photo binding while a different
            // row (for the same player, joined against a different alias)
            // does not — OPTIONAL joins independently, same reasoning as
            // ParseNameIndexBindings' "keep the first non-null value seen"
            // comment. wdt:P18 is single-valued in practice for a Wikidata
            // person item, so "first non-null" is not a lossy simplification
            // here the way it can be for a genuinely multi-valued property.
            if (entry.PhotoUrl is null && binding.TryGetValue("photo", out var photoValue)
                && !string.IsNullOrWhiteSpace(photoValue.Value))
                entry.PhotoUrl = photoValue.Value;

            // REQ-1207/S-082: same "first non-null value seen" shape as
            // PhotoUrl above — wdt:P413 is effectively single-valued in
            // practice for a Wikidata person item. Reads "positionLabel"
            // (bug fix, 2026-08-02), not "position" — see BuildIntersectionQuery's
            // own comment for why the raw binding is a bare entity URI, never
            // the human-readable string this field is meant to hold.
            if (entry.Position is null && binding.TryGetValue("positionLabel", out var positionValue)
                && !string.IsNullOrWhiteSpace(positionValue.Value))
                entry.Position = positionValue.Value;

            // REQ-1207/S-082: ?dateOfBirth is bound on every row for this
            // player (it's a mandatory, non-OPTIONAL match — ADR-0025's pool
            // filter), so every row should agree; "first non-null seen" is
            // still the defensive shape used throughout this method.
            if (entry.BirthYear is null && binding.TryGetValue("dateOfBirth", out var dateOfBirthValue)
                && !string.IsNullOrWhiteSpace(dateOfBirthValue.Value)
                && TryParseXsdDateTimeYear(dateOfBirthValue.Value, out var birthYear))
                entry.BirthYear = birthYear;

            // ADR-0042/S-079: SPARQL's OPTIONAL semantics mean a player with
            // N aliases and M distinct qualifier combinations can produce up
            // to N×M result rows — dedupe qualifier tuples per player via
            // the HashSet the same way Aliases is deduped above (records get
            // structural equality for free). Only recorded when startTime is
            // actually bound: a row where all three qualifiers are unbound
            // carries zero information, and PlayerCareerStint.StartYear is
            // non-nullable, so there is nothing valid to write.
            if (binding.TryGetValue("startTime", out var startTimeValue) && TryParseXsdDateTimeYear(startTimeValue.Value, out var startYear))
            {
                int? endYear = binding.TryGetValue("endTime", out var endTimeValue)
                    && TryParseXsdDateTimeYear(endTimeValue.Value, out var parsedEndYear)
                        ? parsedEndYear
                        : null;
                int? appearanceCount = binding.TryGetValue("numberOfMatches", out var numberOfMatchesValue)
                    && int.TryParse(numberOfMatchesValue.Value, out var parsedAppearanceCount)
                        ? parsedAppearanceCount
                        : null;

                entry.CareerStints.Add(new CareerStintQualifiers(startYear, endYear, appearanceCount));
            }

            byQid[qid] = entry;
        }

        return byQid
            .Select(kv => new WikidataPlayerMatch(
                kv.Key, kv.Value.FullName, kv.Value.Aliases.ToList(), kv.Value.PhotoUrl, kv.Value.CareerStints.ToList())
            {
                Position = kv.Value.Position,
                BirthYear = kv.Value.BirthYear,
            })
            .ToList();
    }

    // ADR-0042/S-079: Wikidata's P580/P582 qualifiers come back as full
    // xsd:dateTime strings (e.g. "2015-07-01T00:00:00Z") — REQ-1201-
    // REQ-1206 only needs the year for chronological ordering and a
    // displayable year range, never month/day precision, so this takes just
    // the leading 4 digits rather than parsing the full timestamp.
    private static bool TryParseXsdDateTimeYear(string xsdDateTime, out int year) =>
        int.TryParse(xsdDateTime.AsSpan(0, Math.Min(4, xsdDateTime.Length)), out year);

    internal sealed record SparqlResponse([property: JsonPropertyName("results")] SparqlResults? Results);

    internal sealed record SparqlResults([property: JsonPropertyName("bindings")] List<Dictionary<string, SparqlValue>>? Bindings);

    internal sealed record SparqlValue([property: JsonPropertyName("value")] string Value);
}
