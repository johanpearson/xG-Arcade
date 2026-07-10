# ADR-0015: A PlayerOverride replaces an entire attribute type, not one value within it

- **Status:** Accepted
- **Date:** 2026-07-10
- **Related requirements:** REQ-203, REQ-501
- **Related components:** COMP-06 (Data.PlayerStore)

## Context

REQ-203 requires that "an override always takes precedence over synced/
unverified data," and REQ-501 requires that "override always wins." Neither
requirement specifies the exact mechanics of what "wins" means once
`PlayerAttribute` is multi-valued per type — which it already is by design
(`PlayerAttribute.cs`'s own doc comment: "a player has one row per distinct
attribute, e.g. one per career club").

S-009 needed to implement the first real reader of override precedence
(`IPlayerStoreRepository.HasEffectiveAttributeAsync`, used by
`GridGameModule.ScoreSubmissionAsync` to check whether a guessed player
satisfies a cell's category). `PlayerOverride`'s shape is singular —
one `(PlayerId, Field)` row holding one `Value` — which doesn't, on its
own, say whether that one value is meant to *replace the entire set* of
cached values for that field, or *correct one specific value* within a
multi-valued set while leaving the others alone.

Concretely, for a player with two cached `club` attribute rows (Arsenal,
Barcelona), an admin adding an override of `club → Arsenal` to fix an
unrelated data error could mean either:

1. "This player's club data should only ever resolve to Arsenal" (the
   Barcelona row becomes ineffective for correctness-checking), or
2. "Arsenal is definitely correct; leave whatever else is cached alone"
   (Barcelona stays effective too).

This is exactly the kind of choice CLAUDE.md's ADR trigger describes:
reverting or changing it later requires knowing which of these two was
actually intended, and S-012 (the future admin override-CRUD story) needs
to build its write-side UI/validation against the same assumption this
read-side check already makes, not a different one discovered later.

## Decision

**A `PlayerOverride` for `(PlayerId, Field)` replaces the entire attribute
type for correctness-checking purposes** — `HasEffectiveAttributeAsync`
checks only the override's `Value` when one exists for that field, and
never falls through to checking any cached `PlayerAttribute` row of that
type, even ones the override doesn't mention.

```
HasEffectiveAttribute(playerId, attributeType, attributeValue):
    if PlayerOverride exists for (playerId, attributeType):
        return PlayerOverride.Value == attributeValue
    return PlayerAttribute exists for (playerId, attributeType, attributeValue)
```

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Override replaces the whole type (chosen)** | Simple, one rule, matches `PlayerOverride`'s existing singular shape exactly — no schema change needed | An admin correcting one wrong value must be aware every other cached value of that type becomes ineffective too, even ones that were never wrong | Chosen: correct for the actual Tier 0 admin use case (S-012's acceptance criterion is literally "override flips a cell's correctness" — a full-type override does that unambiguously) and needs no new entity shape |
| Override adds/corrects one value, others stay effective | Matches an admin's likely mental model of "fix this one wrong value" more closely for a multi-valued field like club | `PlayerOverride`'s `(PlayerId, Field) → Value` shape has no way to represent "this specific cached value is wrong" (it can't reference *which* `PlayerAttribute` row it corrects) — would require a schema change (e.g. `PlayerOverride` referencing a specific `PlayerAttribute` row, or a `PlayerAttribute.IsOverridden` flag) that Tier 0 doesn't otherwise need | Deferred: no Tier 0 story needs per-value override precision yet; revisit if S-012 or real admin usage shows the full-type-replacement behavior is confusing in practice for multi-valued fields (club) specifically |
| Override *adds* a guaranteed-correct value on top of cached ones (union, never hides anything) | Never silently hides previously-correct data | Doesn't actually satisfy "override always wins" for the case an override exists specifically to correct/deny a wrong cached value (e.g. Wikidata wrongly cached "club: Barcelona" and the override says "actually only Arsenal") — REQ-501's whole purpose is to let an admin override *wrong* data, not just add confirmed-correct data alongside it | Rejected: contradicts REQ-501's explicit purpose |

## Consequences

- Positive: `HasEffectiveAttributeAsync`'s implementation is a single,
  simple rule with no new schema, and unambiguously satisfies REQ-501's
  "override flips a cell's correctness" acceptance criterion.
- Negative / trade-offs accepted: for a multi-valued field (`club`), one
  override silently makes every other cached value of that type
  ineffective, even ones the override never mentioned and that may still
  be correct. An admin fixing one wrong club entry must re-add the other
  legitimately-correct clubs as separate `PlayerOverride` rows (there's no
  way to add more than one `Value` per `(PlayerId, Field)` today) or accept
  that only the overridden value is checked going forward.
- Follow-up: S-012 (admin `PlayerOverride` CRUD) must build its UI/copy
  around this exact semantic — e.g. warn an admin overriding `club` that
  it replaces every cached club value for that player, not just adds a
  correction. Revisit if that story (or real usage afterward) shows this
  is confusing enough in practice to warrant the schema change described
  in the rejected alternative above.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
