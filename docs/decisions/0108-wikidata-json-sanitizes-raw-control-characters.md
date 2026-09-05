# ADR-0108: WikidataClient sanitizes raw control characters before JSON parsing

- **Status:** Accepted
- **Date:** 2026-09-05
- **Related requirements:** REQ-207, REQ-103
- **Related components:** COMP-07 (DataSync)

## Context

`import-player-name-index` (REQ-207, ADR-0007's own follow-up) has failed
its scheduled run twice in a row (2026-08-23, 2026-08-30) and again on a
manually-triggered run the same day this ADR was written (2026-09-05).
Both confirmed failures include a `JsonException` for the SAME birth year
(1983) with the SAME error: `'0x0A' is invalid within a JSON string. The
string should be correctly escaped.`

This is not WDQS flakiness. Wikidata's own SPARQL-results JSON endpoint
has, for at least one 1983-born footballer's record, emitted a raw,
literal line-feed character (0x0A) directly inside a JSON string value (a
`?countryLabel` binding) instead of escaping it as `\n`, per RFC 8259's
requirement that control characters inside a JSON string must be escaped.
`System.Text.Json`'s `Utf8JsonReader` correctly rejects this as malformed
JSON — there is no configuration flag (unlike, say,
`JsonReaderOptions.AllowTrailingCommas` for a different class of
leniency) that tells it to tolerate a raw control character inside a
string.

This directly blocks ADR-0107's fix: `PlayerNameIndex.WikidataQid` can
only disambiguate a same-name collision (the real "Jonas Olsson"
incident) for players whose birth-year slice has actually imported
successfully — and 1983 is exactly the slice needed for that real,
reported bug. The same defect also silently affects every OTHER Wikidata
query this client makes whenever the same class of malformed response
occurs (the five intersection queries swallow a parse failure to `[]`,
"no match," rather than crashing — so this bug's blast radius extends
well beyond the one job that happens to surface it loudly).

## Decision

`WikidataClient`'s two shared response drivers (`RunIntersectionQueryAsync`,
`RunThrowingQueryAsync`) now read the HTTP response body as text and run it
through a new `SanitizeControlCharacters` method before handing it to
`JsonSerializer`, instead of streaming the response directly into the
deserializer as before. Both drivers funnel through one shared private
helper, `ReadSanitizedSparqlResponseAsync`, rather than duplicating the
read/sanitize/deserialize sequence — a `quality-architect` review of the
first version of this fix flagged the duplication (and the resulting gap
where only one driver had a direct reproduction test), so this was
extracted before merge. `SanitizeControlCharacters` replaces every raw
ASCII control character (0x00-0x1F, which includes the tab/LF/CR
characters that are also legitimate whitespace *between* JSON tokens) in
the ENTIRE response body with a single space — it does not attempt to
distinguish "inside a JSON string" from "insignificant whitespace between
tokens."

This is deliberately simpler than writing a real JSON-aware scanner that
tracks string-literal state, and is safe either way: a control character
between tokens (legitimate formatting whitespace) becomes a different,
equally-insignificant whitespace character, and one erroneously embedded
inside a string value becomes a plain space, fixing the malformed JSON.
No field this client actually reads and uses (labels, names, QIDs, years,
counts) depends on preserving an embedded literal newline or other control
character — they are all short display/identifier strings, never
multi-line content this codebase parses further.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Sanitize raw control characters globally before parsing (chosen) | Fixes the confirmed failure and any future occurrence of the same class of malformed response, in the one shared place every Wikidata query already funnels through; no JSON-aware scanning needed | Lossy for the rare case a label genuinely needs to preserve an embedded newline (not a real requirement for any field this app reads) | Directly closes a confirmed, reproducible production failure with a fix proportionate to the actual defect, reusing the existing shared driver rather than special-casing one query method |
| Write a JSON-aware scanner that only replaces a control character found strictly inside a string literal (tracking quote/escape state) | More "correct" in principle — never touches legitimate structural whitespace (though replacing that with a space is harmless anyway) | Meaningfully more code and its own test surface, to guard against a distinction (inside-string vs. between-tokens) that doesn't actually matter for JSON's own semantics — inter-token whitespace is fungible | Solves a problem the simpler fix doesn't actually have; not worth the complexity |
| Retry the failing slice indefinitely / treat it as a permanent skip | No code change | The bug is deterministic for this data — it will fail identically on every retry, and `import-player-name-index` already retries 3× per year before giving up (this is exactly what happened, twice) | Confirmed not a transient failure; retrying a deterministic bug never succeeds |
| Report the malformed response to Wikidata and wait for them to fix their own serializer | Correct root-fix, upstream | Out of this codebase's control, no timeline, and this app needs the data now | Reasonable to also do, but not a substitute for defending against malformed input this app has no control over |

## Consequences

- Positive: closes a confirmed, reproducible `import-player-name-index`
  failure and unblocks ADR-0107's own fix for the real "Jonas Olsson"
  same-name-collision incident, which specifically needs birth year 1983's
  slice to succeed.
- Positive: fixes the identical defect class for every other Wikidata
  query this client makes, not just the one job that happened to surface
  it loudly (the five intersection queries were silently swallowing this
  exact failure to an empty "no match" result before now).
- Negative / trade-off accepted: any embedded raw control character inside
  a Wikidata label/name value is now flattened to a plain space rather
  than preserved verbatim. Accepted because no field this codebase reads
  from Wikidata has ever needed to preserve one, and the alternative
  (rejecting the whole response) is strictly worse.
- Follow-up: if a future Wikidata response is found malformed in some
  OTHER way `SanitizeControlCharacters` doesn't address, re-read this ADR
  before deciding whether to extend the same sanitization step or add a
  narrower, targeted one — don't assume every future WDQS quirk belongs
  here without checking it's the same class of problem.

## For AI agents

Do not remove `SanitizeControlCharacters` or revert
`RunIntersectionQueryAsync`/`RunThrowingQueryAsync` back to streaming
directly into `JsonSerializer` without re-reading this ADR first — a real,
twice-confirmed production failure (import-player-name-index's birth-year
1983 slice) depends on this sanitization step running.
