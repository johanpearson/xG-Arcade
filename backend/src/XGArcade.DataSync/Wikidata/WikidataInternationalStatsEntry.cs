namespace XGArcade.DataSync.Wikidata;

// REQ-1501/REQ-1506 (xG Higher/Lower, S-231, ADR-0112): one player's
// resolved international-caps/international-goals figures, as returned by
// QueryInternationalStatsByQidsAsync — the P1350 ("number of matches
// played")/P1351 ("number of goals scored") qualifiers on the single P54
// statement ADR-0112's "highest recorded caps value wins" tie-break
// selects among a player's national-team-membership statements (any P54
// statement whose team carries wdt:P1532 — see
// SparqlQueryBuilders.BuildInternationalStatsByQidsQuery's own comment).
//
// Caps is non-nullable: a QID is only ever present in
// QueryInternationalStatsByQidsAsync's result dictionary when at least one
// candidate statement had a usable pq:P1350 value to select a winning
// statement by — a QID with no such statement is simply absent from the
// result entirely (never present with a placeholder Caps), the same
// "absent means none, never an error" contract every other by-QID Wikidata
// query in this file already uses.
//
// Goals is independently nullable — Wikidata's coverage of the two
// qualifiers is not guaranteed to be symmetric per statement, so the
// WINNING (highest-caps) statement may not itself carry a resolvable
// pq:P1351 value even when Caps did resolve.
public record WikidataInternationalStatsEntry(int Caps, int? Goals);
