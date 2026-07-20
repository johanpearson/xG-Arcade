---
doc_id: design-document
title: UX & Design Document
version: "0.39"
status: draft
last_updated: 2026-07-20
owner: Johan
related_docs:
  - requirements-document.md
  - architecture-document.md
  - implementation-document.md
id_prefix: SCREEN
read_before: ["requirements-document.md"]
update_when:
  - "A new screen or flow is added"
  - "The token system (color/type/layout) changes"
  - "A component's states or copy change in a way that affects other screens"
---

# UX & Design Document – xG Arcade (working title)

Version 0.39 · 2026-07-20
References: `requirements-document.md`, `implementation-document.md`

> **This document describes the full system, not what's being built right
> now.** See `MVP-SCOPE.md` (repo root) — e.g. SCREEN-02's name
> suggestions imply autocomplete, which is Tier 1; Tier 0 is plain text
> input with the same visual shell.
>
> **For AI agents:** this document defines what the product should LOOK and
> FEEL like. When implementing any frontend component, read this document
> first, then read `/mnt/skills/public/frontend-design/SKILL.md` before
> writing code — that skill has the concrete environment constraints
> (available Tailwind classes, React conventions). Derive every color/type
> choice from the token system in §2, don't introduce new ad-hoc values.
> **Revision note:** v0.1 was a dark "broadcast scoreboard" direction. It
> read as generic dark-mode-with-accent rather than distinctive, so v0.2
> replaces it — light, clean, and letting real football imagery (flags,
> club badges) carry the personality instead of a dark palette. Don't carry
> over v0.1's dark tokens or split-flap animation; they're superseded.

## 1. Direction

**Brief:** modern and clean, not dark. Lean on real football imagery —
flags and club badges — rather than a broadcast-graphics palette.

**Why this, not the obvious defaults:** the previous direction (dark
background + gold/teal accents) was a reasonable idea but landed as generic
"dark mode analytics dashboard" — a look that has nothing to do with
football specifically and reads as an AI default regardless of subject.
Football already has strong, recognizable visual identity built in: flags
and club crests are instantly legible symbols people already know. The
distinctive move here isn't inventing a new color language — it's getting
out of the way and letting those symbols do the work, on a clean light
surface that doesn't compete with them.

**Imagery note:** flags are rendered as standard flag emoji/Unicode — safe,
universal, no licensing concern, shipped in v1. Club crests are **deferred
to Phase 2** — v1 ships with the placeholder circular initial-badges shown
throughout this document as the actual design, not a temporary stand-in.
Real crest sourcing via API-Football (`ClubCrest` caching, see
`implementation-document.md` and ADR-0007/0008) is designed and ready to
build, but intentionally not part of v1 scope — see
`requirements-document.md` §6. When it does ship, the initial-badge becomes
the fallback for any club without a crest on file, not something removed
entirely.

## 2. Token system

**Color** (light theme only for v1):

| Token | Hex | Use |
|---|---|---|
| `bg-base` | `#FBFBFA` | App background — a very slightly warm off-white, not stark clinical white and not the generic cream-default either |
| `surface-card` | `#FFFFFF` | Grid cells, cards — pure white against the slightly warm base, so cards lift without needing a shadow to do all the work |
| `surface-sunken` | `#F1F2F0` | Empty/inactive cells, input backgrounds — recessed relative to cards |
| `text-primary` | `#1A1F1C` | Primary text — near-black with a faint green undertone, not pure black |
| `text-muted` | `#6B7570` | Secondary text, labels, captions |
| `border-hairline` | `#E4E6E3` | All dividers and card borders — thin, quiet, never a heavy box |
| `accent-green` | `#1E9E63` | Live/active states, primary actions — a clean pitch green, crisp rather than dark/muted. Non-text/decorative use only (live-dot, focus ring, tab underline) — see `accent-green-text` below for text/icon/button-label use |
| `accent-gold` | `#C99A2E` | Reserved for future non-text/decorative correct/locked-final use (e.g. a Phase 2 badge fill) — see `accent-gold-text` below for text/icon use, which is everywhere Tier 0 actually paints "correct" today on a light background. **Exception (2026-07-18, REQ-214):** this token, not `accent-gold-text`, is the correct choice for the checkmark/points text/icon overlaid on the `overlay-scrim` token below — see that row for why the darker/lighter split flips on a dark backdrop |
| `accent-red` | `#C4463C` | Incorrect states — a muted brick red, not an alarm red. Passes text contrast as-is (~4.9:1 on white) — no separate text variant needed |
| `accent-green-text` | `#187E4F` | **(S-013)** Green text/icon labels, and white-on-green button-label backgrounds (`.guess-input__submit`, `.auth-screen__submit`) — `accent-green` itself measures ~3.4:1 against `surface-card`/white, below WCAG AA's 4.5:1 for normal text; this darkened variant measures ~5.1:1 |
| `accent-gold-text` | `#8D6C20` | **(S-013)** Correct/locked-final text and icons (`CellState`'s correct icon + meta line) — `accent-gold` itself measures ~2.6:1 against `surface-card`/white, failing even the 3:1 floor for large text/icons; this darkened variant measures ~4.9:1 |
| `accent-green-scrim` | `#23B874` | **Exception (2026-07-19, REQ-214, direct user feedback on the shipped photo-fill-cell treatment):** the color of the checkmark glyph only (never the points value beside it, which stays `accent-gold` per the `overlay-scrim` row below) when it's overlaid on a correct cell's at-rest photo. Neither existing green token clears WCAG AA's 4.5:1 floor against the scrim's own worst-case blended background (`rgb(51, 56, 53)` — see `overlay-scrim`'s row for the full derivation): `accent-green` (`#1E9E63`) measures **3.49:1**, and `accent-green-text` (`#187E4F`), being darker still, measures even lower — both fail. This is therefore a new value, not a reuse of an existing token: same hue as `accent-green` (152°), same saturation (68%), lightness raised to 43% (from `accent-green`'s 37%) — `#23B874` — measured at **4.65:1** against the same `rgb(51, 56, 53)` worst-case backdrop, ~3% above the 4.5:1 floor as a safety margin against rendering variance, matching the margin `overlay-scrim`'s own gold math targets. One percentage point of HSL lightness lower (42%, `#22B470`) drops to 4.45:1 and fails — 43% is the practical floor at whole-percent lightness granularity, the same style of verification `overlay-scrim`'s 89%-vs-88% check used. **This is a deliberate, one-off semantic exception, not a new general-purpose "correct" color:** every other correct-state signal in the app — this table's `accent-gold-text`, and `accent-gold` on this very scrim for the points value sitting right beside this same checkmark — is gold, per "Green means live/active, gold means settled/correct" below. The user explicitly asked for this one checkmark, and only this one, to render green instead, after seeing the shipped gold-on-photo treatment; it does not extend to any other correct-checkmark instance in the app (the non-photo checkmark elsewhere in this table remains `accent-gold-text`, unchanged) and must not be reused elsewhere as a general "correct" color without the same explicit, direct call. **Dormant as of 2026-07-19 (S-048):** the checkmark this token was calibrated for no longer renders anywhere on a photo cell (S-048 removed it from both the at-rest and revealed states, per direct user feedback — see `SCREEN-01a`'s S-048 status note). The token and its verification math are kept, not deleted — same "document, don't silently drop" approach as every other superseded value in this table — in case a checkmark is deliberately reintroduced to this overlay later; it must not be reused for any other purpose without a fresh explicit call, same as before. |
| `overlay-scrim` | `rgba(26, 31, 28, 0.89)` | **(2026-07-18, REQ-214; lightened same day after visual feedback that the original 94% read as a heavy black shadow, not a scrim)** Backdrop behind the checkmark/points value (and the name/badge dock, once revealed) when they're overlaid on a correct cell's at-rest photo (`SCREEN-01a` states 1/4's photo mocks) — a bottom-anchored band behind that content only, not a wash across the whole photo. Same hue as `text-primary`. Opacity was chosen as the *lightest* value (most photo showing through) that still clears WCAG AA's 4.5:1 contrast floor for both overlaid foreground colors, measured against the *worst case* (a pure-white photo showing through the remaining 11%), not a typical photo — relative-luminance formula, `rgb(26, 31, 28)` alpha-blended over `#FFFFFF`: at 89%, the blended backdrop is `rgb(51, 56, 53)`, giving `accent-gold` (`#C99A2E`) a contrast ratio of **4.65:1** and `surface-card`/white a ratio of **11.99:1** against it — both clear 4.5:1, with `accent-gold` (the tighter of the two) landing ~3% above the floor rather than exactly on it, as a safety margin against rendering variance (anti-aliasing, photo compression artifacts) rather than relying on an exact knife-edge value. One point lower, at 88%, `accent-gold` drops to 4.49:1 and fails — 89% is therefore the practical floor at whole-percent granularity. Against a typical (non-white) photo the effective contrast is higher still, since most real photos are darker than pure white. **On this token specifically, use `accent-gold` (not `accent-gold-text`) for the overlaid points text/icon** — the reverse of every other text/icon use in this table: `accent-gold-text` was darkened *because* `accent-gold` fails contrast on a light (`surface-card`/white) background, but that same lighter, more saturated `accent-gold` is what actually clears 4.5:1 on this dark background; `accent-gold-text` would under-perform here (calibrated the opposite direction) and must not be reused on this token. **(2026-07-19 update)** the checkmark glyph specifically no longer follows this same gold pairing — see `accent-green-scrim` above, added the same day after direct user feedback asking for the checkmark (not the points value) to be green on this scrim; the gold pairing described in this paragraph still governs the points value and remains correct for it. **The revealed name (REQ-212) also sits on this scrim once shown, and needs the same treatment** — it has no correct/incorrect semantic color of its own (unlike the checkmark/points), so it normally renders in `text-primary` (near-black), which is illegible here for the same reason `accent-gold-text` is: use `surface-card` (white) for the name specifically when it's shown on this scrim, the lightest neutral already in this table rather than a new token. **(2026-07-19 update, S-048):** this scrim itself is now only ever painted once a photo cell is revealed (never at rest — see `SCREEN-01a`'s S-048 status note), and only ever carries the name and points — the checkmark no longer shares this backdrop at all, so `accent-green-scrim` above is currently unused; the `accent-gold`-for-points and `surface-card`-for-name pairings described in this paragraph remain exactly as verified. |

Green means "live/active," gold means "settled/correct" — same semantic
split as before, just recolored for a light surface. This distinction is
load-bearing (REQ-205) so it must stay consistent everywhere. Flags and
badges bring in their own natural colors on top of this neutral shell —
the UI is deliberately quiet so those images read clearly, not muddied by
a busy background.

**Acknowledged exception (2026-07-19, REQ-214):** the checkmark overlaid
on a correct cell's at-rest photo is `accent-green-scrim` (see §2's table
row above), not gold — a direct, explicit user request scoped to that one
glyph, made after seeing the shipped gold-on-photo treatment. This breaks
the green/gold split described in this paragraph for that single instance:
the photo-overlay checkmark still means "correct," same as everywhere
else, but is rendered in the "live" hue. It is recorded here plainly as a
deliberate one-off, not a reinterpretation of the rule — every other
correct-checkmark instance in the app (including the points value sitting
directly beside this same checkmark) is still gold, and any future
correct-state color choice should still default to gold unless someone
makes the same kind of explicit call again. **Dormant as of 2026-07-19
(S-048):** the photo-overlay checkmark this paragraph describes no longer
renders at all — S-048 (see `SCREEN-01a`'s status note) removed the
checkmark from the photo overlay entirely, at rest and revealed alike, per
further direct user feedback. This exception and its token are kept for
the record, not deleted, in case a checkmark is reintroduced there later.

**Text vs. decorative contrast (S-013, resolves §6's former open
item):** §6's contrast floor requires verifying gold-on-white and
green-on-white use once real components existed — S-013 measured both
(WCAG relative-luminance formula against `surface-card`/`#FFFFFF`) and
found `accent-gold` and `accent-green` both fail the required ratio when
used as text/icon color or as a solid button fill behind white
label text (2.6:1 and 3.4:1 respectively, against a 4.5:1 normal-text /
3:1 large-text-and-graphical-object floor). `accent-gold-text`/
`accent-green-text` above are darkened, same-hue variants that pass; the
original tokens remain defined for non-text/decorative use, where the
lighter, more saturated hue was the deliberate intent and the applicable
floor (3:1, non-text UI components) is already met (e.g. `accent-green`'s
live-dot against a card background measures the same ~3.4:1, which
clears 3:1 fine for a decorative indicator).

**Type:**

| Role | Typeface | Notes |
|---|---|---|
| Display / headings | Space Grotesk | Geometric, slightly technical, carried over from v0.1 — it works independently of the light/dark question |
| Body / UI | Inter | Restrained, quiet — personality lives in imagery and data, not body text |
| Data / numerals | IBM Plex Mono, tabular figures | Every score, percentage, and countdown — keeps numbers precise and comparable at a glance |

Rule unchanged from v0.1: any number meant to be compared at a glance is
always in the mono face with tabular figures.

**Layout concept:**

- The grid remains the hero — never compressed for a headline treatment.
- Flags and badges are always paired with their text label, never used
  alone as the only identifier (accessibility — see §6) — a cell reads
  "🇫🇷 France × [AFC badge] Arsenal," not just two icons.
- Generous whitespace on `bg-base`, with cards as the only bordered
  elements — avoids the boxy, templated-dashboard feel that a dark theme
  with heavy card borders tends toward.
- Hairline dividers, not shadows, separate sections — kept from v0.1,
  still correct for a clean direction.

**Signature element: badge dock.** When a player clicks/taps a locked,
correct cell to reveal the guessed player (REQ-212, SCREEN-01a), the row's
flag/badge and the column's badge slide inward from either side and settle
next to the now-visible player name — a small, literal "match" animation
tied directly to the game's actual mechanic (combining two categories), not
a borrowed broadcast trope. This replaces v0.1's split-flap animation,
which was a retro-broadcast flourish that didn't fit a clean, light
direction. **S-041 note:** before that story, this animated at guess-submit
and round-close instead, since the name was shown automatically at one of
those two moments; now that the name is never shown until the player
actively reveals it, the animation moved to that reveal moment instead
(replaying on every reveal, not just the first) — same animation, same
visual meaning ("badges settle beside a newly-visible name"), just tied to
the new trigger that's actually meaningful under S-041's interaction model.
Respects `prefers-reduced-motion`: badges appear already docked, no slide,
with a brief background color flash (green→gold) instead.

**S-047 exception:** on a correct cell that has a photo (SCREEN-01a's
fill-cell photo treatment), the badge dock is hidden on reveal instead of
docking beside the name — real-browser verification found the confined
photo-overlay scrim genuinely doesn't have room for both badges and a
legible name at a typical Tier-0 mobile cell width. See SCREEN-01a's
S-047 status note for the full finding and fix. The no-photo case
described above is completely unaffected.

**Rejected-guess cue (S-020).** When a submitted guess is rejected, the
cell gives a literal, immediate "no match" cue: a brief lateral shake
paired with a red background flash that fades back to transparent.
Mechanically and visually distinct from the badge dock above — it's
triggered by a *rejection*, not a match, uses a shake rather than a slide,
and never touches the badge-dock elements or its keyframes. Fires on
every rejected guess (whether or not an attempt remains afterward), never
on a page load that shows a cell already incorrect. Respects
`prefers-reduced-motion`: flash only, no shake.

## 3. Key screens

### SCREEN-01: Grid (home)

```
Mobile (single column)                Desktop (grid + side panel)
┌─────────────────────────┐           ┌───────────────────────────────────┐
│ Round #14  ⏱ 1d 4h  (ⓘ) │           │ Round #14  ⏱ 1d 4h  (ⓘ) [Leagues▾]│
├─────────────────────────┤           ├───────────────────┬───────────────┤
│      [AFC] [MIL] [BAY]  │           │                    │  Your progress │
│ 🇫🇷 │ Henry│  +  │  +  │           │   3x3 / NxN grid   │  2/9 answered  │
│ 🇧🇷 │  +  │ Kaká │  +  │           │   (same as left)   │                │
│ 🇪🇸 │  +  │  ✕  │  +  │           │                    │  ~69 pts       │
│                          │           │                    │  estimated     │
│ ~69 pts estimated         │           └────────────────────┴───────────────┘
└─────────────────────────┘
```

**S-029:** the running total shown here (REQ-206) uses the same "~N pts
estimated" wording as a single cell's own live point value (REQ-204/S-018)
— it's the sum of whatever per-cell live estimates are already known, not a
promise of the locked total the leaderboard shows once the round closes.
Only shown once at least one cell's live point value is known (never a
fabricated "0" while nothing has been correctly guessed yet). This is a
running-total display, distinct from SCREEN-01a's per-cell value — S-041
only simplified the latter; this line's "~N pts estimated" wording is
unchanged.

**S-041 addition:** the `(ⓘ)` entry point next to the round timer opens
SCREEN-06, the general scoring/live-updates explainer (REQ-213) — see that
section for content and interaction. It replaces the per-cell disclosure
SCREEN-01a used to carry (see that section's own S-041 note) as the one
place a player learns what a live vs. locked point value means, instead of
that explanation being repeated, cell by cell, across the grid.

- Row headers: flag + country name when the row category is a nationality;
  a club badge + club name when the row category is a club (REQ-107 means
  a grid is always Club×Club or Club×Country, never Country×Country, so at
  most one axis is ever flags — the other is always badges).
  Column headers follow the same rule for whichever axis they represent.
- An empty cell shows a faint "+" with no imagery — imagery only appears
  once a cell has an answer, so an unanswered grid doesn't feel cluttered.
- A correct cell (live or locked): checkmark plus a points value only — see
  SCREEN-01a's S-041 note for the full redesign; no per-cell live/final
  distinction exists anymore.
- An incorrect cell with an attempt remaining: red cross, "N attempt(s)
  left" text (SCREEN-01a state 2, unaffected by S-041).
- An incorrect, locked cell (out of attempts, or the round closed):
  red cross plus a points value only — see SCREEN-01a state 3's S-033
  note; no "no attempts left"/"final" qualifier text, matching a correct
  cell's own "checkmark plus points, nothing else" structure above.
- Desktop's side panel is additive only — mobile gets the same information
  stacked below the grid.

**Status note (2026-07-14):** only the mobile single-column layout above
has actually been built — the desktop side-panel variant shown in the mock
was never implemented; every viewport currently gets the single-column
layout, stretched to `.app`'s `max-width: 900px` cap. Direct product
feedback found this reads as small/stuck-top-left with unused space around
it on a genuinely wide viewport, since the layout was never art-directed
past that cap. `docs/backlog.md` S-040 polishes the single-column layout's
own spacing/sizing at wide viewports; the side-panel variant itself remains
explicitly deferred to a separate, not-yet-scoped future story, not
silently dropped.

### SCREEN-01a: Cell states (component, appears within cells)

Four distinct states now exist per REQ-210, not two — correctness is
revealed immediately (REQ-203), separate from whether the round has closed:

**1. Correct, round still active** (locked from further guessing, score
still live until round close):

```
At rest, no photo (default when the resolved player has none):
┌─────────────────────────┐
│                     ✓     │   ← gold checkmark — no dot, no "live" text,
│  12 pts                   │     no name until clicked/tapped
└─────────────────────────┘

At rest, photo available (2026-07-19, S-048 status note — supersedes the
2026-07-18 mock this replaced, which showed a scrim-backed checkmark/points
row here even at rest; see the S-048 status note after state 4 below for
the full rationale and trade-off):
┌─────────────────────────┐
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
│▒▒▒▒▒▒[ player photo,▒▒▒▒▒│    ← photo only — no checkmark, no points
│▒▒▒▒▒▒fills cell]▒▒▒▒▒▒▒▒▒│      value, no scrim/overlay of any kind at
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│      rest — the picture is the only thing
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│      shown until the player clicks/taps
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│
└─────────────────────────┘

Revealed, no photo (click/tap the cell — toggles closed again on a
second click/tap; unchanged from before this note):
┌─────────────────────────┐
│  Henry                ✓   │
│  12 pts                   │
└─────────────────────────┘

Revealed, photo available (2026-07-19, S-048 status note — same click/tap
toggle; the photo itself does not react to the toggle, only the overlay
below does):
┌─────────────────────────┐
│▒▒▒▒▒▒[ player photo,▒▒▒▒▒│
│▒▒▒▒▒▒unchanged ]▒▒▒▒▒▒▒▒▒│
│▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│    ← scrim strip carrying only the name
│▓ Henry                    │      and points — no checkmark here (S-048;
│▓ 12 pts                   │      the no-photo case above still has one)
└─────────────────────────┘        and no badge dock (already dropped,
                                     S-047)
```

**REQ-214 status note (2026-07-18): photo decoupled from the click/tap
reveal.** Supersedes this section's own "Revealed, photo available" mock as
it read immediately after REQ-214 first shipped (photo appearing only once
revealed, alongside the name) — requested directly by the user after seeing
that version live. The photo now shows automatically at rest, filling the
cell, whenever the resolved player has one; the click/tap toggle (REQ-212,
unchanged) continues to govern only the name and badge dock, and no longer
gates the photo at all. Practically: a correct cell with a photo now shows
that photo immediately once locked, before the player has clicked/tapped
anything; clicking/tapping it afterward adds the name on top (and over the
photo, if present) exactly as REQ-212 already specified, without changing
whether the photo itself is showing. The checkmark and points value are
overlaid on the photo (a scrim/shadow strip behind them, shown as the `▓`
band above) rather than sitting on a plain card background — see this
section's REQ-214 implementation note below for why no dedicated overlay
token exists yet for this treatment. The no-photo mock and behavior above
are unaffected by this note. **Superseded in part by S-048 (2026-07-19,
see that status note below):** the "checkmark and points value are
overlaid on the photo" sentence above described the *at-rest* photo cell
as first shipped — as of S-048 the checkmark/points no longer appear at
rest on a photo cell at all, only the picture itself; the scrim/overlay
treatment this paragraph describes now only ever appears once the cell is
revealed, and carries the name and points, never the checkmark. The
photo-decoupled-from-reveal mechanism this note is otherwise about (the
photo shows automatically, independent of the click/tap toggle) is
unchanged by S-048.

**S-041 redesign (supersedes S-040's mock above):** further direct product
feedback found the live/final distinction S-040 preserved (a pulsing dot,
the word "live," the "~N pts estimated" qualifier, and the S-019 tap/hover/
focus toggle revealing a %-breakdown + round-end-time line) was itself
unnecessary noise — a player doesn't need any of that per cell to know
their score, just the number. The dot, "live" text, "~"/"estimated"
wording, and the whole %-breakdown/round-end disclosure are gone. At rest:
checkmark plus the live point estimate, full stop — identical in structure
to state 4 below (see that state's own note). A player cannot tell from
the cell alone whether the shown value could still change before round
close; that's now explained once, generally, by SCREEN-06's explainer
(REQ-213), not repeated per cell. **Exception (S-048, 2026-07-19):** this
"checkmark plus points at rest" rule no longer holds for a correct cell
that has a photo — see the S-048 status note after state 4 below for the
photo-specific at-rest and revealed treatment, which now shows only the
picture at rest and only the name/points once revealed. This paragraph's
rule is otherwise unchanged for every cell without a photo. What the %-breakdown disclosure used to
gate (the player name + badge dock) is now gated by a **click/tap
anywhere on the cell** instead — replacing S-019's three-way click/hover/
focus toggle on a small in-cell button with one interaction, the same on
every device (REQ-212). `aria-expanded` on the cell itself still reflects
open/closed state, so keyboard/screen-reader access is unchanged in kind,
just simpler in mechanism. When no live point value exists yet (a guess
just submitted, value not back from the server), the cell shows the
checkmark with no points line at all — the name still isn't shown until
clicked, same click/tap interaction either way (there is no longer a
"nothing to disclose, so skip the toggle" fallback S-019/S-040 needed,
since the click target is the whole cell rather than a button next to
optional live text).

**Superseded by S-041 (kept for history):** the S-040 mock's dot/"live"
text/always-visible "~N pts estimated" qualifier, and the S-019/S-029/S-040
tap-or-hover/focus toggle revealing "N% of others guessed this too · ~N pts
estimated" plus "updates until round closes on [date/time]." None of that
content is shown per cell anymore — see SCREEN-06.

**2. Incorrect, one attempt remaining:**

```
┌─────────────────────────┐
│                     ✕      │   ← red cross, not locked — no name shown
│  1 attempt left           │   ← always spelled out, never just an icon
└─────────────────────────┘
      ↑ rejected-guess cue (S-020) plays once here: a brief shake + red
        flash, distinct from the badge dock above — see §2
```

**S-029:** a wrong guess shows no name at all, not even the text the
player typed — just the ✕ and the attempt count. Earlier versions of this
mock (and the shipped code, until now) showed the as-typed guess
("Ronaldinho" above) even when wrong; a player-feedback pass found this
unhelpful (a wrong guess isn't useful information) and, worse, inconsistent
with the *correct* case's canonical-cased name (a wrong guess showed
whatever casing the player happened to type). Removed entirely for the
incorrect states rather than partially fixed.

**3. Incorrect, no attempts remaining** (round still active, cell is done):

```
┌─────────────────────────┐
│                     ✕      │   ← no name shown, same as state 2
│  100 pts                  │   ← guaranteed worst score (ADR-0021) —
└─────────────────────────┘      same minimal structure as a correct
      ↑ same rejected-guess cue     cell, no extra qualifier text
        plays here too, on the
        guess that used up the
        last attempt
```

**Simplified (2026-07-14), reported directly by a player:** this state
used to also spell out "no attempts left" alongside the points ("no
attempts left · 100 pts") — once the points value itself was added
(S-033), that qualifier read as redundant: the points alone already say
"this cell is done," the same way a correct cell's points say so without
needing to add "correct" in words. Dropped in favor of matching a correct
cell's own minimal "✕/✓ + points, nothing else" structure exactly. This
also now applies uniformly to state 4's incorrect outcome below (round
closed) — both render identically ("✕ 100 pts"), since `MaxPointsPerCell`
is the same guaranteed value regardless of *when* the cell locked, and a
player can't (and per REQ-204 shouldn't need to) tell from the cell alone
whether the round itself is still active or already closed; see SCREEN-06
for where that's explained generally instead. State 2 (an attempt still
remains) is unaffected — "N attempt(s) left" stays, since that's genuinely
actionable information, not a redundant status label.

**ADR-0021:** an incorrect/exhausted cell locks at `MaxPointsPerCell` (100
by default), not 0 — xG Arcade is scored like golf, so 0 is the *best*
possible score and must never be free just for guessing wrong.

**4. Round closed** (either prior state, now permanent):

```
Prior outcome: correct (at rest)      Prior outcome: incorrect
┌─────────────────────────┐           ┌─────────────────────────┐
│                     ✓     │           │                     ✕    │
│  88 pts                   │           │  100 pts                 │
└─────────────────────────┘           └─────────────────────────┘
   ↑ gold checkmark — identical           ← no name here either,
     structure to state 1 at rest           same S-029 rule as states
                                             2/3; points value is the
                                             same MaxPointsPerCell state
                                             3 shows, per today's fix

Prior outcome: correct, no photo (revealed — click/tap the cell)
┌─────────────────────────┐
│  Henry                ✓   │
│  88 pts                   │   ← unchanged at-rest line, stays visible
└─────────────────────────┘

Prior outcome: correct, photo available (at rest — 2026-07-19, S-048
status note; photo shows automatically, no click/tap needed, and nothing
else is overlaid — see the S-048 status note below)
┌─────────────────────────┐
│▒▒▒▒▒▒[ player photo,▒▒▒▒▒│
│▒▒▒▒▒▒fills cell]▒▒▒▒▒▒▒▒▒│   ← picture only, same as state 1's at-rest
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│     photo mock above; no checkmark, no
│▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│     points, no scrim at rest
└─────────────────────────┘

Prior outcome: correct, photo available (revealed — click/tap adds the
name and points on top, same REQ-212 toggle; photo itself unaffected by
the toggle; 2026-07-19, S-048 status note)
┌─────────────────────────┐
│▒▒▒▒▒▒[ player photo,▒▒▒▒▒│
│▒▒▒▒▒▒unchanged ]▒▒▒▒▒▒▒▒▒│
│▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│    ← no checkmark here either — same
│▓ Henry                    │      name+points-only overlay as state 1's
│▓ 88 pts                   │      revealed photo mock above
└─────────────────────────┘
```

**REQ-214 implementation note (frontend half, 2026-07-18, as first
shipped) — superseded, kept for history:** the paragraph that originally
stood here described the photo as a small 18px circular avatar shown
*beside* the name, appearing/disappearing with REQ-212's click/tap reveal
toggle exactly like the name did (reusing `.category-label__badge--small`'s
existing 18px circle token, since no dedicated avatar/photo token existed).
That presentation is no longer current — see the "Photo decoupled from the
click/tap reveal" status note above state 2 and the mocks directly above
this note. The 18px-circle reuse and its "appears/disappears with the
name" behavior are both superseded by the fill-the-cell, always-at-rest
treatment now specified; this paragraph is kept only so the prior shipped
behavior isn't lost from the record.

**REQ-214 implementation note (fill-cell treatment, 2026-07-18 status
note):** the photo now fills the cell's full footprint at rest — same
fixed cell width/height as the no-photo case. **Superseded (2026-07-19,
S-051 — see this section's own S-051 status note below state 4 for the
full detail):** this note originally said `object-fit: cover` here "so
the source image crops to fill rather than distorting or resizing the
cell" — that's now `object-fit: contain` instead, a direct user choice to
show the whole photo (never cropping it) at the cost of possible
letterboxing, not a distortion/resizing concern either mode ever had
(neither `cover` nor `contain` ever distorts an image's own aspect
ratio — only `cover` crops it to avoid empty space, and `contain` avoids
cropping at the cost of empty space; "resizing the cell" was never
actually at stake for either value, since the cell's own box is sized
independently of the image either way, per the mechanism the rest of this
note describes). Mechanically, the photo layer is taken out of the cell's own normal-flow
box (absolutely positioned, filling the button's full box edge-to-edge,
deliberately ignoring the button's own padding so the photo can bleed to
the cell's corners as the mock shows) rather than being sized by its own
content — the same "the image can never grow the box" guarantee the
now-superseded 18px avatar-circle used a fixed pixel size for, just
achieved differently now that the photo fills the whole cell instead of a
small slot. **§2 now has a real `overlay-scrim` token** (added the same
day this note was written) for the text-or-icon-on-photo contrast problem
this note used to flag as an open gap — a solid, near-opaque bottom band
behind the checkmark/points (and the name/badge dock, once revealed), not
a wash across the whole photo; see that token's own row for the exact
value, the worst-case-photo contrast verification, and the
`accent-gold`-not-`accent-gold-text` foreground-color call that goes with
it (the two darkened/lightened token pairs in this document are calibrated
for opposite background directions — light `surface-card` vs. this dark
scrim — so the "always use the darkened `-text` variant" habit that holds
everywhere else in this document is specifically wrong on this one token).
No broken-image icon, no loading spinner, and no error text for a missing/
failed photo — that state is visually and behaviorally identical to
today's no-photo at-rest display (same DOM shape, no scrim/overlay layer
rendered at all in that case). Cell footprint (width/height) is a literal
constant regardless of whether a photo is present, absent, or fails to
load — this is a hard, testable constraint (REQ-214), not a visual
preference.

**S-047 status note (2026-07-19, direct user feedback on the shipped
fill-cell photo treatment — the overlay covers too much of the photo on
mobile):** real screenshots (mobile, cells roughly 90-110px tall in a
Tier-0 3x3 grid) showed the `▓` scrim band above covering roughly 40-45%
of the cell — the checkmark/points row plus, once REQ-212's click/tap
reveal also puts the name/badges into that same row, the wrapped second
line that typically forces on a narrow cell — well past this section's
own original mock, which implied roughly 30% (≈2 of 6 ASCII rows). Fixed
primarily by shrinking the overlay's own footprint at rest (the common
case, no clipping involved) — see the last bullet below for the revealed
case, which needed a different, clipping-based fix after real-browser
verification found the original no-clip plan didn't hold up:
- The overlay's padding drops from a uniform `--space-2` (8px) to
  `--space-1` `--space-2` (4px vertical / 8px horizontal) — reusing the
  existing spacing tokens, no new value.
- The photo variant specifically (extending the existing
  `.cell-state--photo .cell-state__meta`/`.cell-state--photo
  .cell-state__icon--correct` override pattern) renders smaller than the
  no-photo case: checkmark 11px (was 14px), points/meta text 10px (was
  11px), and the revealed name 12px with a tightened 1.2 line-height
  (was an un-set ~16px/1.5 browser default) — the no-photo cell's type
  sizes are unchanged.
- The row's internal gap (badges/name/icon) tightens from `--space-2`
  (8px) to `--space-1` (4px) on the photo variant only, so wrapping is
  less likely and less tall when it does happen.
- **Numeric target:** at rest (checkmark + points only, the common case),
  the overlay should occupy no more than **~35% of the cell's height** on
  a typical mobile cell in the 90-110px range. These are targets achieved
  through the padding/type reductions above. No change to `overlay-scrim`'s
  color/opacity or the `accent-gold`/`accent-green-scrim`/`surface-card`
  foreground pairings above — shrinking padding/type size doesn't change
  any contrast ratio, so none of that math needed re-verification.
- **Revealed state — two further bugs found during this story's own
  required real-browser verification, corrected before shipping (not
  anticipated in the original bug description; the plan below this bullet
  was the original intent, superseded by what's described after it):**
  the plan was to let a revealed photo cell's overlay grow up to ~55% of
  the cell's height (badges + wrapped name + checkmark/points) without a
  hard clip, on the reasoning that clipping via `overflow: hidden` risked
  cutting off a long name worse than a slightly-oversized overlay. Real-
  browser verification showed this was wrong on both counts:
  1. `.cell-state--photo`'s own `overflow: hidden` (needed so the *photo*
     doesn't bleed past the cell's rounded corners) already clips
     anything inside it that grows taller than the cell — there was no
     way to opt out of clipping for the overlay specifically while keeping
     it for the photo, so "avoid clipping" was never actually achievable
     with this structure. Worse, since the overlay is bottom-anchored and
     content grows *upward* out of view, clipping happened from the
     *top*, which for a 2-line name showed an unpredictable *middle*
     fragment (e.g. "izecson..." from "Ricardo Izecson dos Santos Leite")
     rather than the name's actual beginning.
  2. At a typical Tier-0 mobile cell's content width (~65-80px), the
     revealed row's four flex items (row badge, name, column badge,
     checkmark) didn't fit on one line for *any* real name, not just long
     ones — "Thierry Henry" rendered completely invisible, not just
     tightly cropped.
  **Fixed by, on the photo variant only:** hiding both badge-dock glyphs
  once revealed (they're decorative/`aria-hidden` and already redundant
  with the row/column category headers shown above/left of the whole
  grid), and clamping the name to a single line with a trailing ellipsis
  (`-webkit-line-clamp: 1`) instead of letting it wrap. This narrows (does
  not remove) the "signature badge-dock" element described above to the
  no-photo case — a deliberate, explicitly-recorded one-off in the same
  spirit as `accent-green-scrim`'s checkmark-color exception, made because
  the confined photo-overlay context genuinely can't fit all four elements
  legibly at Tier-0 mobile widths, not a change of mind about the
  badge-dock's value generally. The no-photo case's badge dock (and its
  slide-in animation) is completely unaffected. A one-line, ellipsis-
  truncated name is an accepted trade-off — the full name remains in the
  DOM (so nothing is lost to assistive tech), and showing "Ricardo..."
  reliably beats showing a random unreadable fragment or nothing at all.
- **A gradient fade (the user's other suggested option, "gradient/
  bottom-bar treatment instead of a solid block") was considered and
  rejected for this pass:** the existing `overlay-scrim` contrast math is
  verified against one flat, worst-case alpha value (89%, see that
  token's own row); a gradient would need per-point contrast
  re-verification anywhere text could sit within the fading region, which
  is exactly the kind of subtle regression this file has already had to
  correct twice (the 94%→89% opacity change and the checkmark's
  green-scrim exception). Shrinking the solid band's footprint gets the
  same "covers less of the photo" outcome the user asked for without
  reopening that contrast math. Revisit a gradient treatment later if
  product feedback specifically asks for the softer visual edge, not just
  less coverage.

**S-048 status note (2026-07-19, direct user feedback on the shipped S-047
treatment — "at rest, only picture. on click name + points only in an
overlay"):** a further, deliberate simplification of the photo case, not
another coverage tweak — supersedes S-047's photo mocks above (and the
checkmark's overlaid treatment generally) with a narrower rule:
- **At rest, a correct cell with a photo now shows the photo and nothing
  else** — no `.cell-state__overlay`, no scrim, no checkmark, no points
  value. This is a change to the *at-rest* case specifically; the no-photo
  at-rest treatment (checkmark + points, state 1/state 4's original mocks
  at the top of this section) is completely unaffected.
- **On click/tap (revealed), the overlay now shows only the player's name
  and the points value** — no checkmark icon, and no badge dock (already
  dropped by S-047; stays dropped, not reintroduced). The scrim/contrast
  treatment behind them (`overlay-scrim`, `accent-gold` for points,
  `surface-card` for the name) is unchanged — none of that math needed
  re-verification, since it's the same two foreground colors on the same
  backdrop, just without the checkmark sharing the row. The checkmark's
  own `accent-green-scrim` exception (§2 above) is consequently unused as
  of this story — see that token's row for the note recording this rather
  than deleting the token outright, since it's still a documented,
  intentional exception should a checkmark ever return to this overlay.
- **Trade-off, recorded rather than silently assumed:** before this story,
  a photo cell's checkmark+points was the only always-visible, at-a-glance
  signal that the cell was "done" and roughly how well it scored, without
  clicking each one — this was REQ-204's original point. A photo-filled
  cell now carries none of that signal at rest; the only always-visible
  fact is that the cell has a photo, which itself already implies a
  correct, locked guess (an incorrect or unattempted cell never has one),
  so a player can still infer "this one's solved" from the photo alone,
  just not the score. This is the user's own explicit trade-off, made
  directly ("at rest, only picture"), not a default this document is
  inventing a justification for after the fact — recorded here plainly
  per this repo's own discipline for exactly this kind of call. The
  no-photo case keeps its always-visible checkmark+points exactly as
  REQ-204 originally specified; this trade-off is scoped to the photo case
  only.
- **What stays exactly as-is:** the photo's own at-rest trigger (automatic,
  independent of `revealed` — REQ-214's 2026-07-18 decoupling is
  unaffected), the click/tap toggle mechanism itself (REQ-212, same
  whole-cell target, same `aria-expanded`, same keyboard/mouse/touch
  parity), the fixed-cell-footprint guarantee, and the overlay's own
  padding/type-size treatment from S-047 (still applicable to the name and
  points that do render on reveal).

**S-050 (2026-07-19):** the `▒` fill in this section's own at-rest photo
mock above was always meant to touch all four sides of the box border —
it didn't, in the version shipped through S-049, by a real, measured,
symmetric margin. See §4's "Grid cell photo fill" note for the root cause
and fix (CSS-only, `Grid.css`); nothing in this section's own mocks or
click/tap behavior changed.

**S-051 status note (2026-07-19) — direct product decision, not a bug
fix.** The user asked directly: "I want the full picture to be visible
within the cells, so they are not cut off," referring to `object-fit:
cover`'s crop-to-fill behavior (every mock in this section showing a
uniform `▒` fill implied a photo that reaches every edge with nothing
cropped away, but `cover` actually crops whatever doesn't fit the cell's
own aspect ratio — the two aren't the same thing, and the shipped
behavior was the latter). Asked to choose between "Crop photo to fill the
cell completely (today's behavior)" and "Show full photo, allow empty
space (letterbox)," after being told the trade-off explicitly (a
differently-shaped photo may leave a thin background strip on two
opposite sides of the cell) — the user chose the letterbox option.
`object-fit: contain` (was `cover`) on `.cell-state__photo-img`
(`CellState.css`) is the mechanical change: the whole photo now always
renders, scaled down to fit entirely within the cell, at the cost of
empty space appearing on two opposite sides whenever the photo's aspect
ratio doesn't match the cell's own (roughly square, per §4) — top/bottom
for a wider-than-cell (landscape) photo, left/right for a
taller-than-cell (portrait) one. Every `▒▒▒▒▒▒` fill-block mock in this
section (states 1 and 4, at rest and revealed) should now be read as "the
photo, scaled to fit, possibly with a plain background strip on two
sides" rather than a literal uniform fill — the mocks are ASCII art and
were never going to show this distinction precisely either way, so they
are not redrawn here.
- **Letterbox background:** `.cell-state--photo` (the box the image sits
  in) now has its own explicit `background-color: var(--color-surface-card)`
  (`CellState.css`) — before this story it had none, and relied on
  `.grid-cell`'s (the button behind it, `Grid.css`) own
  `background: var(--color-surface-card)` showing through its transparent
  box. That fallthrough happened to already be the right, clean color
  (confirmed via real-browser screenshots at both a mobile and a desktop
  viewport, with a genuinely non-square test photo at each orientation:
  the letterbox strip reads as a plain white card background, not a
  visible seam or an obviously "wrong" color) — but it was incidental, not
  guaranteed: nothing tied the two together, so a future change to
  `.grid-cell`'s own background (e.g. a new hover/selected-state treatment)
  could have silently changed what letterboxing looks like without anyone
  touching the photo code at all. Made explicit on `.cell-state--photo`
  itself instead, now that this box's own background is actually visible
  and load-bearing (never true when `cover` guaranteed the image reached
  every edge). Same token, same value — this is a robustness fix to *how*
  the color is guaranteed, not a visual change.
- **Overlay contrast over the letterbox, re-verified rather than
  assumed:** `overlay-scrim`'s existing contrast math (§2 above) was
  calibrated against "the worst case: a pure-white photo showing through
  the remaining 11%." `--color-surface-card` (`frontend/src/index.css`) is
  `#FFFFFF` — literally pure white, not an off-white tint — so a landscape
  photo's bottom letterbox strip (the case that can land directly behind
  the bottom-anchored overlay) presents the scrim with *exactly* the same
  underlying color the existing math already treats as the worst case,
  not merely a similar one: alpha-blending is agnostic to whether the
  white behind it is "a very light photo" or "an opaque background
  color," so the same `rgb(51, 56, 53)` blended value, the same 4.65:1
  (`accent-gold`) and 11.99:1 (`surface-card`/white, the revealed name's
  color) ratios already recorded under `overlay-scrim` apply unchanged.
  **No new token or contrast math was needed** — confirmed by checking the
  actual token value (not assumed), and re-confirmed visually via
  real-browser screenshots of a revealed landscape-oriented photo cell
  (bottom letterbox landing behind the overlay): the name and points text
  remained clearly legible against the scrim in that exact scenario. A
  portrait-oriented photo's letterbox lands left/right, never behind the
  bottom-anchored overlay at all, so it was never a contrast concern to
  begin with.
- **Unaffected by this change, re-confirmed rather than assumed:** the
  fixed-cell-footprint guarantee (REQ-214) — the mechanism is the
  absolutely-positioned box's own `inset: 0`/explicit `width`/`height`,
  never the fit mode, and real-browser measurement across landscape and
  portrait photos at both breakpoints (mobile/desktop) showed identical
  cell dimensions regardless of photo orientation or fit mode; and the
  no-photo/load-failure at-rest display, which only ever concerns a
  *successfully loading* photo's own presentation and is untouched here.

**S-041 redesign (supersedes S-040's mock above):** same redesign as state
1 above, applied here too — no more dot/"live"/"final" text distinguishing
a closed cell from a still-live one (a player can't tell which from the
cell alone, by design; see SCREEN-06), and no more %-breakdown disclosure.
At rest: checkmark plus `FinalPoints`, identical in structure to state 1's
checkmark-plus-live-estimate — the two states differ only in *which*
points value is shown, not in how it's displayed. Click/tap anywhere on
the cell reveals the guessed player's name and badge dock, same single
interaction as state 1 (REQ-212), replacing S-040's reveal toggle (which
itself had replaced no toggle at all, before that). The incorrect-outcome
half of state 4 is unaffected by the reveal/name change specifically — it
already showed no name (S-029) and still isn't a click target, since
there's nothing to reveal — but its points display was simplified
alongside state 3's (see that state's own 2026-07-14 note): both now show
"✕ 100 pts" with no "no attempts left"/"final" qualifier, using the
frontend's own `MAX_POINTS_PER_CELL` constant directly rather than a
`FinalPoints` value plumbed from the API — the incorrect-lock value is the
same guaranteed constant regardless of whether the cell locked mid-round
or the round closed around it, so there's nothing round-status-specific
left to compute or wait on here.

"Attempt(s) left" still always appears as text, never color/icon-only
(REQ-204, accessibility) for state 2, the one state that still has
actionable text to show. State 1 and state 4 (and, as of today, state 3)
are deliberately *not* visually distinguishable by round status at
rest — see SCREEN-06 for where that distinction is explained instead.

### SCREEN-02: Guess input

Bottom sheet on mobile, inline popover on desktop — unchanged structurally
from v0.1, recolored for the light theme:

```
┌─────────────────────────────┐
│ 🇫🇷 France × [AFC] Arsenal   │
│ 1 of 2 attempts used          │   ← only shown once at least 1 attempt used;
│ ┌───────────────────────────┐│      an untried cell shows no attempt count at all
│ │ Type a player name...     ││
│ └───────────────────────────┘│
│ 👤 Thierry Henry              │
│                               │
│         [ Submit guess ]     │
└─────────────────────────────┘
```

- Autocomplete rows show a small silhouette/placeholder avatar next to the
  name where a player photo is available — optional, degrades to text-only
  cleanly if no photo exists, never a broken-image icon.
- The category header itself now doubles as instant visual confirmation of
  what's being asked, via the flag/badge — reduces reliance on reading the
  category names carefully under time pressure.
- Autocomplete is sourced from the broad name index (REQ-207), not the
  narrower validation data — so suggestions appear for many names that
  won't turn out to be correct for this specific cell. That's intentional,
  not a bug to fix visually; nothing in this screen should imply a
  suggested name is already known to be right.

**S-032 implementation note:** shipped without the photo/silhouette avatar
described above — the `PlayerNameIndex`-backed contract this story builds
against (ADR-0007) carries `name`/`birthYear`/`nationality` only, no photo
field, so each suggestion row instead shows the name plus an optional
`nationality · birthYear` caption line in `text-muted` for disambiguation.
Avatar support stays an open item if/when the index gains a photo field.
Judgment calls made without an existing spec to follow, recorded here
rather than left as unreviewed implementation-only detail:
- Suggestions list uses only neutral tokens — `surface-card` background,
  `border-hairline` dividers, `text-primary`/`text-muted` for name/caption,
  and `surface-sunken` (the same "recessed" token already used for an
  untouched input, not a live/correct accent) for the keyboard-highlighted
  row. Deliberately no `accent-green`/`accent-gold` anywhere in this list —
  either would visually suggest a name is "probably right," undermining
  REQ-207's own point.
- Selecting a suggestion (click, or Enter on the keyboard-highlighted row)
  fills the text field only — never auto-submits — so the player always
  takes an explicit, separate "Submit guess" action regardless of how the
  name got into the field.
- Debounced at 275ms after the last keystroke, once the trimmed query
  reaches 2 characters; a failed suggestions fetch is swallowed
  client-side (shows no suggestions, never blocks or errors the guess
  form) since autocomplete is a nice-to-have, not required to submit.
- Standard combobox/listbox ARIA pattern (`role="combobox"` on the input,
  `role="listbox"`/`role="option"` on the suggestion list, with
  `aria-activedescendant` tracking the arrow-key-highlighted option) —
  arrow keys move through suggestions, Enter picks the highlighted one
  (or falls through to the form's normal submit if nothing is
  highlighted), Escape dismisses the list without clearing typed text.

### SCREEN-02a: Disambiguation prompt

Appears only when a submitted name matches more than one real player who
*both* satisfy the cell's categories (REQ-209) — genuinely rare, but must
be handled cleanly rather than silently guessing on the player's behalf.

```
┌─────────────────────────────┐
│ Which Ronaldo did you mean?  │
│                               │
│ ○ 👤 Ronaldo (b. 1976)        │
│    Brazil · Real Madrid       │
│                               │
│ ○ 👤 Ronaldo (b. 1993)        │
│    Brazil · Real Madrid       │
│                               │
│         [ Confirm ]          │
└─────────────────────────────┘
```

- Single-select list, each option showing enough to actually distinguish
  them (birth year always; nationality/club shown even though both share
  the searched category here, since it still helps recognition).
- This is the only place a bare "which one?" choice is acceptable without
  more context — the alternative (guessing on the player's behalf, or
  rejecting a genuinely correct answer) is worse in both directions.
- If the player abandons this prompt without choosing, the guess is not
  submitted — it does not default to either candidate.

### SCREEN-03: Leaderboard

```
┌───────────────────────────────┐
│ [Global] [My League ▾] [+ New] │
│ Lowest total wins               │
├───────────────────────────────┤
│ 1  Sam         120 pts         │
│ 2  You         138 pts   ← you │
│ 3  Alex        142 pts         │
├───────────────────────────────┤
│         [ Load more ]          │
└───────────────────────────────┘
```

Unchanged from v0.1 structurally — tabs for Global vs. custom leagues, the
user's row always visually distinct. Recolored: the user's row uses
`surface-sunken` instead of a dark raised surface.

**Pagination (REQ-607, S-034):** a "Load more" control below the list
fetches and appends the next page — outline-fill button (`surface-card`
background, `border-hairline`, `accent-green-text` label), not a second
green CTA. When the requesting user's row isn't among the currently
loaded page(s), a pinned "you" row renders below the list (same
`surface-sunken`/"you"-tag treatment as an in-list row, sticky to the
viewport bottom) so their standing is always visible without loading
further pages. No new tokens — both reuse the existing surface/border/
accent set above.

**ADR-0021 addition:** xG Arcade is scored like golf — lowest total wins,
the opposite of the natural "higher number = better" assumption most
players will bring from other games. The "Lowest total wins" line (plain
text, `text-muted` token, no new color) is added directly under the tab
row specifically to correct that assumption before a player reads any
rank — it must never be omitted or left implicit in the ranking order
alone. Rank #1 is always the lowest `TotalPoints`, consistent with
`LeaderboardService`'s ascending sort.

**Scope selector (REQ-406/407/408, S-053/S-054 — backfilled here 2026-07-20,
this section had not been updated when those stories shipped) and Time
Windows (REQ-405, S-027, added 2026-07-20):** a row of scope tabs sits
above the ranked list, distinct from the `[Global] [My League ▾] [+ New]`
league tabs above (those stay a deferred mock per `MVP-SCOPE.md`; this
selector exists alongside them, not instead of them):

```
┌───────────────────────────────────────────┐
│ [All-time] [Current Round] [Previous       │
│  Rounds] [Time Windows]                    │
│ Lowest total wins                          │
├───────────────────────────────────────────┤
│  (Time Windows only)                       │
│  [Round] [Week] [Month] [Year]             │
├───────────────────────────────────────────┤
│ 1  Sam         120 pts                     │
│ 2  You         138 pts               ← you │
│ 3  Alex        142 pts                     │
├───────────────────────────────────────────┤
│               [ Load more ]                │
└───────────────────────────────────────────┘
```

Same underline-tab treatment as `.auth-screen__tabs`/`.auth-screen__tab`
(`accent-green` underline on the active tab) — one visual tab pattern
reused, not a second one invented. Four scopes:
- **All-time** (REQ-401/404): the existing locked, all-time global
  leaderboard, unchanged, and the default scope shown on first load.
- **Current Round** (REQ-407/ADR-0031): the active round's own
  leaderboard, recomputed live on every read. Rows and the running total
  render with the same "~N pts estimated" wording SCREEN-01's live cell
  value already uses (never presented as a locked final), with an
  explicit "Live — estimated, can still change until the round closes."
  note under the tabs. "No round is currently active — check back once
  one starts" is a plain informational empty state (not an error) when
  nothing is active; "No one has played this round yet" is the separate,
  distinct empty state for an active-but-unplayed round.
- **Previous Rounds** (REQ-408): a browsable list of closed rounds
  (labeled by their `closedAt` timestamp — there is no round-number field
  to fall back on), drilling into one round's own locked, final
  leaderboard (plain "N pts", never "estimated").
- **Time Windows** (REQ-405, S-027): a rolling leaderboard summed only
  over locked `FinalPoints` within a fixed window, never live/provisional
  points — so, like Previous Rounds, its rows always render plain "N pts",
  never "estimated". Selecting this scope reveals a second, visually
  quieter row of round/week/month/year sub-tabs directly below the
  top-level tabs (same `role="tab"`/`aria-selected` pattern, smaller
  font-size, no bottom border of its own — a nested row, not a second
  competing tab bar) — "Round" is the default sub-tab, since it's the
  most specific/recent window and the one closest to what "Current Round"
  already trains a player to check. Switching sub-tabs re-fetches that
  resolution's leaderboard. An empty ranked list here (nothing has
  happened in that window yet) is a real, calm empty state — "No one
  scored in this window yet." — never an error.

Re-entering **Current Round**, **Previous Rounds**, or **Time Windows**
after visiting a different scope always issues a fresh request and briefly
shows a loading state again, rather than silently leaving a previous,
possibly-stale response on screen — each of these three scopes' whole
value proposition is "check back for something more current," so a loading
flash is the more honest signal on re-entry than quiet staleness. **All-time**
is the one exception: its 15-second background poll runs continuously
regardless of which scope tab is active, so switching back to it never
shows a loading flash — the data was never stale to begin with.

### SCREEN-04: Admin (unverified data review, round control, user deletion)

Still deliberately plainer/denser than the rest of the product — a working
tool, not a broadcast surface. On the light theme this now reads as a
clean, ordinary admin table rather than needing its own "un-dark" treatment.
Reached only by a user whose id is in `Admin__UserIds` (REQ-504) via a link
that itself only renders for that user — nothing resembling an entry point
is shown to anyone else, and every underlying endpoint independently 403s a
non-admin token that reaches it directly (defense in depth, not just
nav-hiding).

**Status note (2026-07-19, entry point relocated per REQ-712/REQ-713):**
that link no longer lives as a standalone top-level header item — it's now
SCREEN-08's admin-only link, itself reached via SCREEN-07's "Settings" nav
entry. `AdminScreen` itself, its authorization checks, and the
Production-only section-hiding described below are all unchanged; only how
a player navigates here changed, one hop further from the header than
before.

**S-026 status note:** this section previously described only the
unverified-data review list as an aspirational mock (`[Approve]`/
`[Correct]`/`[Remove]`), with no page actually built. S-026 built the real
page, and in doing so found `Approve`/`Remove` were never implemented as
backend actions at all (REQ-503's status note) — only `Correct` (creating a
`PlayerOverride`) exists. Dropped from the mock below rather than shipped
as dead buttons for endpoints that don't exist, the same rule REQ-504
states explicitly for the round-control/user-deletion sections'
Production gating. The two sections below (round control, user deletion)
are new as of S-026.

**Status note (2026-07-20, REQ-503's "approve" extension):** `Approve` is
back, in bulk-first form — `POST /admin/player-data/approve` now exists
server-side (bulk, a single id is just the N=1 case), so the mock below
adds a checkbox per row, a "select all" control, a selected-count readout,
and an "Approve selected" button. `Remove` still doesn't exist server-side
and is still not shown. No new tokens: the checkbox reuses the exact sizing/spacing the
login/signup screen's REQ-701 age-confirmation checkbox already
established (`AuthScreen.css`'s `.auth-screen__checkbox` — 20×20px box,
`--space-2` gap, `--touch-target-min` row height; that screen still has no
formal `SCREEN-xx` entry of its own, §7's open item) rather than inventing
a second checkbox style, and the failed row color reuses `accent-red` —
the same token this document already uses for every other error/incorrect
state, not a new "failure" color.

**Unverified data review (REQ-501/502/503) — always rendered, no
`ASPNETCORE_ENVIRONMENT` gate:**

```
┌─────────────────────────────────────────────┐
│ Unverified data (14)                          │
├─────────────────────────────────────────────┤
│ [ ] Select all         3 selected  [Approve   │
│                                     selected] │
├─────────────────────────────────────────────┤
│ [✓] Henry · nationality · France · live_lookup│
│       [Correct]                                │
│ [ ] Mbappe · club · PSG · wikidata             │
│       [Correct]                                │
│ ...                                            │
└─────────────────────────────────────────────┘
```

After an approve submits, a persistent results list appears above the row
list until dismissed:

```
┌─────────────────────────────────────────────┐
│ Henry · nationality · France — Approved.       │
│ Mbappe · club · PSG — Not approved — already   │
│   reviewed by someone else.                    │
│ [Dismiss]                                      │
└─────────────────────────────────────────────┘
```

Empty state: plain text, "No unverified data to review." (design-document.md
§5: empty states are invitations, though there's nothing to invite here
beyond "nothing to do right now"). `[Correct]` reveals an inline form
(value + reason) — submitting calls `POST /admin/player-overrides`; on
success the list is refetched. A 409 (an override already exists for that
player/field) shows the server's own detail text inline rather than
crashing — there's still no dedicated "edit an existing override" UI (S-012
never built a browsable override list), so an admin hitting this picks a
different row for now.

Each row's own checkbox (not a substitute for `[Correct]` — both actions
exist independently on the same row) selects it for the bulk approve
below; "Select all" selects every row currently loaded in the view, not
every unverified row that exists server-side (this view has no pagination
yet, so today they're the same set, but the control's own meaning is
scoped to what's on screen). "Approve selected" is disabled at zero
selected. Submitting calls `POST /admin/player-data/approve` with every
selected id — no reason field, unlike `[Correct]`'s form. The response is
always a per-row result, never a single pass/fail for the whole batch: the
results list above shows each selected row's own outcome ("Approved." or
"Not approved — " plus what happened, e.g. "this row no longer exists" or
"already reviewed by someone else" — never the raw `NotFound`/
`NotUnverified` value shown as-is), and the underlying row list is
refetched the same way `[Correct]`'s successful submit already does — a
row that succeeded drops out of the refetched list (it's no longer
unverified), a row that failed stays in the list precisely because its
`Confidence` is still whatever it already was, so an admin can act on it
again in either case: it's readable directly, no separate lookup needed.

**Round control (REQ-505) — entirely absent from the page, not merely
disabled, when `ASPNETCORE_ENVIRONMENT == Production` (the round-control
probe endpoint itself 404s there — see REQ-505's fail-closed pattern):**

```
┌─────────────────────────────────────────────┐
│ Round control — xg-grid                       │
├─────────────────────────────────────────────┤
│ Round R-14 · ends 2026-07-20T18:00:00Z        │
│                                                 │
│ [ End round now ]                              │
│   (click reveals) → [Yes, end round now] [Cancel]│
│                                                 │
│ New end time [__________________] [Update end  │
│                                     time]       │
└─────────────────────────────────────────────┘
```

When no round is active, "No active round right now." replaces the
"Round ... · ends ..." line — a normal state (`hasActiveRound: false` is a
routine 200), not an error. "End round now" is destructive and
irreversible, so — same as SCREEN-05's account-deletion precedent — it
uses a two-step, explicit re-confirm (a revealed second button restating
the action, "Yes, end round now") rather than a native `window.confirm`;
unlike SCREEN-05 there is no password step, since being an authenticated
admin is itself the confirmation REQ-505 requires. "Update end time" shows
the server's 400 `detail` text inline on an invalid choice (not after both
the round's start time and the current time).

**User deletion (REQ-506) — same visibility rule as round control above
(hidden entirely outside non-Production, same shared environment gate):**

```
┌─────────────────────────────────────────────┐
│ Delete a user                                  │
├─────────────────────────────────────────────┤
│ Email [__________________]                    │
│ [ Delete user ]                                │
│   (click reveals) → [Yes, delete this user     │
│                       permanently] [Cancel]    │
└─────────────────────────────────────────────┘
```

Same two-step confirm pattern as "End round now." An email with no
matching user shows "No user found with that email." inline rather than a
generic error; a successful deletion clears the field and shows a brief
"Deleted." confirmation. This reuses REQ-710's existing anonymization
behavior under an admin-triggered path — a second trigger for that one
behavior, not a second, independently-designed deletion flow.

### SCREEN-05: Delete account

```
┌───────────────────────────────┐
│ Delete account                 │
├───────────────────────────────┤
│ This permanently deletes your  │
│ account. It cannot be undone.  │
│                                 │
│ Current password                │
│ [__________________]           │
│                                 │
│         [Cancel] [Delete my    │
│                    account     │
│                    permanently]│
└───────────────────────────────┘
```

**S-039, REQ-710.** Reached only from a plain "Delete account" link in the
header — deliberately not a general profile/settings page (none exists in
Tier 0).

**Status note (2026-07-19, entry point relocated per REQ-712/REQ-713):**
that standalone header link is superseded by SCREEN-08 ("Settings"), which
now hosts this exact, otherwise-unchanged flow — a general settings page
*does* now exist (SCREEN-08), so the "none exists in Tier 0" aside above is
outdated. It is still not a general profile/settings page in the broader
sense: SCREEN-08 adds nothing to this flow beyond its own framing and,
admin-only, a link elsewhere — no other account fields live there. Nothing
below about this screen's own copy, warning, or confirmation step changes.

No bare confirmation checkbox: the "current password" field is the
confirmation step REQ-710 already requires server-side (`AuthController
.DeleteAccount` re-verifies it against Supabase Auth before touching
anything), so the UI can't offer a weaker path than the API already
enforces. The warning line uses `accent-red` (text use, already passes the
4.5:1 floor as-is per §2 — no new token needed) and is not color-only: it's
a plain, explicit sentence, not a colored icon or border standing alone.
"Delete my account permanently" (not just "Delete") states the destructive
action plainly, per §5's "name the action" rule — no confirm-twice modal on
top of the password step, since re-entering a password already is the
confirmation. A wrong password shows an inline error (same `accent-red`
error-text pattern the login/signup form already uses, see §7's open
question on that screen's missing spec) and deletes nothing. On success
there is no account left to show anything else on, so the flow signs the
user out and lands back on the login/landing screen — no "deleted"
confirmation screen, nothing to confirm to once signed out.

### SCREEN-06: Scoring/live-updates explainer

```
┌───────────────────────────────┐
│ How scoring works          [×]│
├───────────────────────────────┤
│ You get 2 attempts per cell.   │
│                                 │
│ A correct cell shows a live    │
│ estimate that can still change │
│ until the round closes.        │
│                                 │
│ Once the round closes, that    │
│ value is locked and won't      │
│ change again.                  │
│                                 │
│ A wrong guess (after both      │
│ attempts) locks in the maximum │
│ score for that cell — the same │
│ maximum score you'd get by not │
│ guessing at all once the round │
│ closes.                        │
│                                 │
│ xG Arcade scores like golf —   │
│ lower is better. An answer     │
│ fewer other players also       │
│ guessed scores better than a   │
│ common one.                    │
│                                 │
│ Answers are footballers who    │
│ are male and born in 1939 or   │
│ later.                         │
└───────────────────────────────┘
```

**S-041, REQ-213.** Opened from the `(ⓘ)` entry point in SCREEN-01's
header, next to the round timer — a modal (`role="dialog"`,
`aria-modal="true"`), structurally the same backdrop-plus-card pattern
SCREEN-02's `GuessInput` already established (backdrop click closes it).
This modal goes further on two points `GuessInput` doesn't (yet — a known,
separate gap, not part of this story): Escape also closes it, and closing
it (by any method) returns focus to the `(ⓘ)` button that opened it,
rather than leaving keyboard/screen-reader focus stranded on a
now-invisible element. Deliberately not a full route/screen: it's a short,
general explanation, never gated behind having attempted any cell, and
reachable at any time an active round is showing. Content is general to the
mechanic, never cell-specific (no "your cell scored 12 pts" — that number
already lives on the cell itself).

**Content expanded (2026-07-14), requested directly by a player:** the
original three paragraphs (what a live estimate means and that it can
change, what a locked/final value means once the round closes, and — in
general terms, no exact formula — the golf-style/fewer-others-guessed
framing already established in SCREEN-03, ADR-0021) are still required,
joined by three more:
- The attempt count (`MAX_ATTEMPTS_PER_CELL`, 2) — a player asked directly
  whether this was documented anywhere, and it wasn't, despite being
  fundamental to how a guess even works (REQ-210).
- That a wrong guess locking at the maximum score (ADR-0021, already true
  and already shown per-cell as of today's SCREEN-01a fix) is the *same*
  maximum score an unanswered cell locks at once the round closes
  (`ScoreLockingService.MaterializeUnansweredCellsAsync`, S-028/ADR-0021)
  — the two were previously each documented in isolation (one per-cell,
  one only in `requirements-document.md`) with nothing connecting them for
  a player reading the explainer.
- The player-pool restriction (REQ-112/ADR-0025: male footballers born
  1939 or later only) — previously undocumented anywhere player-facing at
  all; without it, a technically-correct-but-out-of-scope name being
  rejected as "wrong" would look like a bug rather than an intentional
  scope boundary.

This is where SCREEN-01a's now-removed per-cell disclosure content (the
%-breakdown line, "updates until round closes on [date/time]") effectively
moved — see that section's S-041 note — except reworded to be general
rather than tied to one cell's specific numbers.

### SCREEN-07: Header navigation (mobile menu)

```
┌──────────────────────────────┐
│ xG Arcade            [☰ Menu]│
└──────────────────────────────┘
        ↓ (toggle activated)
┌──────────────────────────────┐
│ xG Arcade            [☰ Menu]│
├──────────────────────────────┤
│ Leaderboard                   │
│ Settings                      │
│ Log out                       │
└──────────────────────────────┘
```

**Added 2026-07-19, REQ-712.** Below the mobile breakpoint (see §4's new
"Header nav breakpoint" note — 480px, reusing the existing narrow-phone
value rather than a new one), the header's nav row collapses behind this
single toggle so it never wraps or overflows regardless of how many
entries exist — this was a real regression (REQ-504 and REQ-710 each added
their own top-level link since S-029 last fixed a header-overflow issue by
trimming items). At/above the breakpoint the row renders exactly as
before: a plain horizontal row, no toggle at all.

The toggle is a real `<button>` — Tab-reachable and Enter/Space-activatable
by native HTML button semantics, no custom key handling needed — exposing
`aria-expanded` for its open/closed state, the same accessible-disclosure
pattern REQ-204's reveal toggles (`SCREEN-01a`, `GridCell.tsx`) already
established. Activating it a second time dismisses the list. **No new
motion:** unlike the badge dock (§2's one deliberate signature animation),
this disclosure is an instant show/hide with no slide/fade transition —
per this doc's own rule against adding a second bold motion moment without
it being specified here first, and there was no reason to specify one for
a plain menu reveal.

Nav entries in the revealed list: "Leaderboard," "Settings" (SCREEN-08,
REQ-713), and "Log out" — see SCREEN-08 for what replaced the previous
standalone "Delete account" and "Admin" links.

### SCREEN-08: Settings

```
┌───────────────────────────────┐
│ Settings                       │
├───────────────────────────────┤
│ [ Admin ]      (admin-only)    │
├───────────────────────────────┤
│ Display name                   │
│ [__________________]           │
│         [ Save name ]          │
│         Display name updated.  │
├───────────────────────────────┤
│ Delete account                 │
│ This permanently deletes your  │
│ account. It cannot be undone.  │
│                                 │
│ Current password                │
│ [__________________]           │
│                                 │
│         [Cancel] [Delete my    │
│                    account     │
│                    permanently]│
└───────────────────────────────┘
```

**Added 2026-07-19, REQ-713.** Reached from SCREEN-07's "Settings" nav
entry, replacing the previously separate standalone "Delete account"
(SCREEN-05) and admin-only "Admin" (SCREEN-04) top-level header links —
see the status notes now on both those sections. Hosts SCREEN-05's
delete-account flow completely unmodified (same component, same copy,
same server-verified password confirmation step, same tests) — this
screen adds no new behavior to it, only the surrounding "Settings" framing
above it. Only when the logged-in user is an admin (the same `isAdmin`
check REQ-504's own nav-link gating already used) does an "Admin" link
also render, above the delete-account section, in its own bordered row —
a plain link out to SCREEN-04's `AdminScreen`, never admin controls
embedded inline on this screen itself. A non-admin sees no trace of that
link, on this screen or in SCREEN-07's nav menu — the same "no visible
entry point" guarantee REQ-504 already makes for `AdminScreen` itself, now
also true of its one remaining entry point. Tokens only (`surface-card`,
`border-hairline`, existing spacing/type scale) — no new visual treatment.

**Added 2026-07-20, REQ-714:** a "Display name" section, between the
admin-only link and the delete-account section, hosting a single-field
form (pre-filled with the account's current name) and a "Save name"
button — same 1-30 character bound and inline-error convention
`AuthScreen.tsx`'s signup form already established for the same field, and
the same "server's own detail text shown inline, not a generic failure
banner" convention `DeleteAccountScreen`'s own 401/409-shaped errors
already use (so a name-taken conflict shows the server's specific message,
not a generic one). A successful save shows "Display name updated." in
`accent-green-text` (the text-contrast-safe green variant, not
`accent-green` — see §2's text-contrast note) directly below the field, and
the caller's own state updates immediately from the server's confirmed
response, with no page reload or refetch needed for the new name to show
up everywhere else it's read. No new tokens — reuses `settings-screen__section`'s
existing bordered-row treatment plus the same field/input pattern
`AuthScreen.tsx` already established.

## 4. Responsive strategy

Unchanged from v0.1 — built "equally both" from the start:

- Layout defined per breakpoint at the component level, not one fluid
  layout reflowing.
- Grid cell minimum touch target: 44×44px on mobile regardless of grid
  size; a 5x5 on a narrow phone scrolls horizontally with sticky row/column
  headers rather than shrinking below that floor. **S-029 correction:**
  this floor only ever applied to the cells themselves — a Tier 0 3×3 grid
  was still forced into horizontal scroll on an ordinary phone because
  row/column header *label text* (a country/club name, nowrap, uncapped
  width — "Paris Saint-Germain," "United Kingdom") was wider than the
  screen, not because of the touch-target floor. Below a 480px viewport,
  header labels now wrap onto two lines and shrink their own width floor
  instead (`Grid.css`); the cell floor and the horizontal-scroll fallback
  itself are unchanged for whatever is still too wide (a larger grid, or a
  longer name still).
- **Grid cell aspect ratio (added S-047, closing a gap this document never
  specified numerically):** a data cell (`.grid-cell`) must render
  square-ish — width:height between **1:1 and ~1.3:1** — at every
  viewport from 481px up through desktop, for a Tier-0-sized grid (≤5
  columns). This was violated in practice: `.grid-table` used
  `width: 100%` unconditionally, which — combined with the browser's
  default `table-layout: auto` above 480px, and `.grid-table__cell`'s
  explicit `height` acting as a floor, not a ceiling, on row height —
  stretched a 3-column Tier-0 grid's cells to fill however wide the
  viewport happened to be (reproducible on any real desktop browser, not
  only via a phone's "Request desktop site," which just happens to report
  a similar ~980-1200px CSS viewport). Fixed by letting the table use its
  own intrinsic (shrink-to-fit) width above 480px instead of forcing
  `width: 100%` — per the CSS2.1 automatic table-layout algorithm, an
  auto-width table only fills its container when a column's own content
  genuinely needs that width; otherwise columns size from their own
  content/`min-width` floor (the existing 44px/64px touch-target tokens),
  which is what keeps them close to square. A grid that genuinely has
  enough columns or long enough names to need the full container width
  still gets it, unchanged — the horizontal-scroll fallback above remains
  the backstop either way. Below 480px, S-040's `table-layout: fixed` +
  explicit `<colgroup>` widths remain exactly as they were (that
  breakpoint's own problem — header text wrapping — needs a deliberate
  full-width fixed layout, not shrink-to-fit) — this rule does not apply
  there. REQ-214's "cell footprint is a literal constant regardless of
  photo presence" constraint is unaffected — this is a table/column-width
  fix, not a per-cell content change.
- **Grid cell target size at desktop (added S-049, extends S-047's
  floor-only rule above — the aspect-ratio bound itself is unchanged):**
  S-047 fixed cells stretching into flat rectangles, but the fix it shipped
  (`.grid-table__cell`'s `min-width`/`height`, 64px at `≥960px`, 44px below
  that) was only ever a **floor**, never a deliberate **target** for a
  genuinely wide desktop viewport. Direct user feedback, after S-047/S-048
  shipped and mobile was confirmed to look good ("if i switch to desktop
  view in the mobile it still looks weird.. feels like the grid could be
  larger? and the cell + picture should look nice"), found the
  consequence: with a Tier-0 grid's 3-5 columns and no cell content that
  ever needs more room than that 64px floor (nothing in `.grid-cell`'s
  content forces a column wider than it — text wraps rather than growing
  the box, and `.cell-state--photo`'s photo layer is absolutely positioned
  out of the normal flow — the same fact S-047 already established for the
  64px value), the grid rendered at its smallest reasonable size — roughly
  300-400px wide — inside `.app`'s 1200px desktop cap, reading as "stuck
  small" rather than substantial. Fixed by raising the same floor the
  table already sizes its shrink-to-fit columns from, not by switching
  mechanism: at `≥960px`, `.grid-table__cell`'s `min-width`/`height` become
  **120px** (up from 64px) and its padding grows from `--space-2` to
  `--space-3` in step, so the bigger footprint isn't just a larger empty
  box around the same tight spacing. Because nothing in a Tier-0 cell's
  content ever exceeds this floor, raising it functions as a de facto
  *target* render size in practice, not just a lower bound — confirmed via
  real-browser verification (not assumed): a 3×3 grid renders at ~490×406px
  and a 5×5 at ~787×646px at a 1280px viewport, both comfortably inside the
  1200px desktop cap with cells reading square (~1.14:1, within the
  existing 1:1–1.3:1 bound above) and no overflow or horizontal-scroll
  fallback triggering. `object-fit: cover` on `.cell-state__photo-img`, as
  it stood at the time this note was written, scaled the photo cleanly to
  the larger footprint with no distortion. **Superseded (2026-07-19,
  S-051):** the fit mode is now `object-fit: contain` (a direct user
  choice, see SCREEN-01a's S-051 status note) — this note's own point
  still holds regardless of fit mode (neither `cover` nor `contain` ever
  distorts the image; the larger footprint scales either mode's output
  cleanly), so nothing about this story's own 120px-floor change needed
  re-verification when the fit mode changed separately. The 481-959px
  shrink-to-fit range and the ≤480px
  `table-layout: fixed` range are both unaffected — this change is scoped
  to the existing `≥960px` breakpoint only. **Superseded (2026-07-20,
  S-055):** the 481-959px range described as "unaffected" here is no
  longer shrink-to-fit-from-content at all — see S-055's own note below for
  why (a different bug, uneven column widths, forced a mechanism change for
  that range specifically) and for the deliberate target size S-055 also
  gives it, closing the gap this sentence originally left open. No change to REQ-214's
  fixed-cell-footprint constraint — this is a target-size increase within
  the same "constant regardless of photo presence" rule, not a relaxation
  of it. **CellState.css companion change:** the photo-overlay's revealed
  name/points type (S-047's 12px/10px, tuned for a ~90-110px *mobile*
  cell) read as undersized once the cell itself nearly doubled — a second
  angle on the same "cell + picture should look nice" feedback. A matching
  `≥960px` override bumps the revealed name to 15px and the points line to
  12px, and the overlay's padding from `--space-1`/`--space-2` to
  `--space-2`/`--space-3`; the existing single-line ellipsis clamp
  (`-webkit-line-clamp: 1`) is unchanged and re-verified at the larger size
  with a deliberately long name ("Ricardo Izecson dos Santos Leite") —
  still truncates cleanly to "Ricardo…" with no clipping/overflow, so no
  change to that mechanism was needed. The no-photo case's own type sizes
  are untouched — real-browser verification found them to already read
  fine at the larger cell size, badge dock and name+checkmark included.
- **Grid cell photo fill (added S-050, closes a gap the S-047/S-048/S-049
  notes above never checked directly):** a correct cell's photo (REQ-214)
  must fill all the way to the cell's actual bordered edge — the same
  literal "filling the cell" intent SCREEN-01a's own at-rest photo mock
  below has always shown (the `▒` fill in that ASCII box touches all four
  sides of the box border, with no blank margin drawn). Direct user
  feedback, with real screenshots at both a mobile and a "Request desktop
  site" viewport, reported a visible white gap between the photo and the
  cell's own border. Root-caused via `getBoundingClientRect` on a real
  Chromium render (not guessed): the gap was real, measured, and
  **symmetric** on all four sides (4px below 960px, 12px at/above it, at
  the time this note was written — S-055 below adds a third padding value,
  8px, for the 481-959px band specifically) —
  not literally bottom-only as first described, though most visually
  obvious where two photo cells stack vertically (that gap, doubled across
  the shared row border, reads as a noticeably wide blank band, which is
  almost certainly what the report was actually describing). Cause:
  `CellState.css`'s `.cell-state--photo` bleeds through `.grid-cell`'s (the
  button's) own padding via `inset: 0` against its padding box, exactly as
  S-047/REQ-214's own comments already documented — but `.grid-table__cell`
  (the `<td>` itself) has a *second*, separate padding layer one level
  further out that was never bypassed, so the photo always stopped short
  of the `<td>`'s actual border by exactly that amount. Fixed by moving the
  `position: relative` that establishes the abs-positioning containing
  block from `.grid-cell` up to `.grid-table__cell` — the photo now bleeds
  through both padding layers, reaching the cell's real edge (confirmed:
  remaining gap after the fix is 0.5px on every side at both breakpoints
  tested, exactly this rule's own 1px border, split by sub-pixel rounding).
  A `:has(.cell-state--photo)`-scoped padding override on the `<td>` was
  tried first and rejected: real-browser verification found it would make
  `.grid-cell`'s own rendered size depend on whether a photo is *currently*
  showing, which `CellState.tsx` ties to load success (a failed image
  unmounts `.cell-state--photo` entirely) — reintroducing exactly the
  "cell resizes if an already-shown photo fails to load" bug REQ-214's
  fixed-footprint guarantee forbids, confirmed via a deliberately-broken
  photo URL before rejecting that approach. The chosen fix has no such
  dependency — `.grid-cell`'s own box is governed solely by its own
  unconditional CSS regardless of photo presence/load outcome, re-verified
  the same way. No change to the aspect-ratio or target-size rules above —
  this only changes how much of the same footprint the photo fills, not
  the footprint's own size.
- **Grid cell uniform column width (added S-055, closes a gap the S-047
  note above assumed away):** every data column in a Tier-0 grid must
  render at the same width, regardless of how long the row/column category
  name in that column happens to be — direct user screenshots of a 3×3
  grid showed "Sevilla"'s column visibly narrower than "Atletico Madrid"'s.
  S-047's own fix (shrink-to-fit `.grid-table` width, `.grid-table__cell`'s
  min-width as a floor) assumed "nothing in a cell's content forces a
  column wider than [the floor]" — true for a single column in isolation,
  but never actually checked *across* columns: `table-layout: auto` (the
  browser default, left in place above 480px since S-047/S-049) sizes each
  column independently from the widest cell/header content in *that*
  column specifically, so a column with a long name still rendered wider
  than a column with a short one, on every breakpoint except ≤480px
  (S-040's own fix already sidesteps this there via `table-layout: fixed` +
  explicit `<colgroup>` widths). Confirmed via real-browser measurement,
  not assumed: before this fix, a 3×3 grid's "Sevilla"/"Atletico
  Madrid"/"Real Sociedad" columns measured 92.75px/147.97px/141.59px at a
  700px viewport and 120px/155.97px/149.59px at 1280px — the bug reproduces
  at desktop width too, just less visibly since the 120px floor there
  (S-049) is already fairly wide. Fixed by making `table-layout: fixed`
  unconditional (previously only inside the ≤480px block) and giving every
  data column an explicit, equal `<col>` width via a new `grid-table__data-col`
  class (`Grid.tsx`'s `<colgroup>`, previously unclassed for data columns) —
  fixed layout takes each column's width from its own `<col>` rather than
  its widest cell, so an explicit, identical width per data column is what
  actually guarantees identical columns. Chosen widths reuse existing
  values where one already existed rather than inventing new ones: 90px for
  the 481-959px band (already `.grid-table__col-header`'s own min-width),
  120px at ≥960px (already `.grid-table__cell`'s S-049-verified target) —
  the row-header column scales in step (110px / 140px). Verified via real
  Chromium render at 390px/700px/1280px with the same mixed-length example:
  every data column now measures identically at each width (89.83px/90px/
  120px respectively), no horizontal-scroll fallback triggers, and the
  ≤480px band's own already-working mechanism (unclassed data `<col>`s
  equally dividing a `width: 100%` table) is unaffected — reset explicitly
  back to `width: auto` inside that block, since the new unconditional 90px
  base rule would otherwise apply there too and disrupt it. Header/row
  label text now wraps (flag/badge stacked above the name, reusing S-040's
  own mobile-only treatment, generalized to every breakpoint) rather than
  stretching its column — a deliberate, undocumented-until-now choice
  (flagged here per this doc's own "no ad-hoc value in code" rule): a plain
  inline layout was tried first and rejected after real-browser
  verification found the ~50-65px of text width left over next to the
  glyph at the 481-959px band's new 90px column still wrapped a longer name
  awkwardly, where stacking gives it the column's full width instead, the
  same reasoning S-040 already established for ≤480px.

  **Aspect-ratio bound closed for the 481-959px band as part of the same
  story:** verifying the width fix above surfaced that the 1:1-1.3:1 bound
  this section's own S-047 bullet requires "at every viewport from 481px up"
  was never actually met in that specific band — S-049's own note explicitly
  scoped its floor-to-target fix to `≥960px` only, leaving 481-959px without
  a deliberate footprint of its own; content alone (badge/flag + text) had
  already been forcing `.grid-table__cell`'s height past its 44px floor to
  ~53-57px, which combined with the (now-fixed) 90px column width measured
  at ~1.7:1 — an improvement over the pre-fix ~2.8:1 the same content-driven
  column-width bug caused there, but still outside the documented bound.
  Closed the same way S-049 already closed it at ≥960px (not a new
  mechanism): a `481px`-`959px` media block raises `.grid-table__cell`'s
  height to match the 90px column width (1:1), with padding stepped up one
  notch (`--space-1` → `--space-2`, short of desktop's `--space-3`). The
  ≤480px band remains explicitly exempt from this bound (unchanged, per
  this section's own S-047 bullet wording) and was not touched. No change
  to REQ-214's fixed-cell-footprint guarantee — this only sizes the
  footprint itself, the same class of change S-049 already made at a
  different breakpoint, not the "constant regardless of photo load
  outcome" rule.
- **Grid uniform row height, ≤480px (added S-059, closes a gap S-055 left
  open on the row axis):** every data row's cells must render at the same
  height, regardless of how many lines that row's own row-header label
  wraps to — the row-axis equivalent of S-055's uniform-*column*-width
  guarantee above, reported the same way (direct user screenshots of a 3×3
  grid, this time at real mobile widths of 390-412px specifically): "Real
  Sociedad" (wraps 2 lines), "Paris Saint-Germain" (3 lines), and "Valencia"
  (1 line) rendered at visibly different row heights, tracking each row's
  own row-header line count. Root cause, confirmed via real-browser
  `getBoundingClientRect` measurement (not guessed): `.grid-table__cell`'s
  `height` is only ever a *floor* on a table row's height, never a ceiling
  — the same CSS2.1 table-layout fact S-047's own note above already
  documents for the column axis, here on the row axis instead. The
  481-959px and ≥960px bands already carry a real, deliberate target height
  (90px/S-055, 120px/S-049) comfortably larger than what ordinary wrapped
  row-header content needs, so they never exhibited this bug (confirmed:
  both rendered uniformly, 90px/120px, before and after this fix); only the
  ≤480px band still relied on the bare 44px `--touch-target-min` floor,
  which every real row-header (a badge/flag stacked above at least one line
  of text, per S-040/S-055's stacking rule above) already exceeds — some
  per-row growth beyond 44px was inevitable, the bug was that it wasn't the
  *same* amount for every row (measured 61px/76px/53px for the three rows
  above, before this fix, at a 390px viewport). Fixed the same way
  S-049/S-055 already closed the equivalent floor-vs-target gap at their
  own breakpoints: `.grid-table__cell` gets a real, explicit **78px**
  target height at ≤480px too (a working number for this grid's own longest
  real content — "Paris Saint-Germain"'s natural 3-line/76px need, plus a
  small rounding margin — not derived from an existing column width the way
  90px/120px each reuse one, since ≤480px is explicitly exempt from this
  section's own aspect-ratio bound above and has no equivalent value to
  reuse). Paired with a **3-line `-webkit-line-clamp`** on the row-header's
  own name text so a label longer than any of this grid's own three
  examples can never exceed that 78px budget and reintroduce the bug for a
  single outlier row — the same truncation-with-ellipsis technique
  `CellState.css`'s `.cell-state--photo .cell-state__name` (S-047) already
  uses, not a new mechanism; the full label text stays in the DOM for
  assistive tech regardless, only its painted box is bounded. 3 lines, not
  fewer, specifically because "Paris Saint-Germain" itself already needs
  exactly 3 to render in full at this column width — a smaller clamp would
  visibly truncate the very label from the real bug report this fixes.
  **Flagged trade-off, verified rather than assumed:** a row-header label
  genuinely needing a 4th wrapped line (none exist in Tier-0's real
  country/club data at the time this note was written) would truncate with
  a trailing ellipsis instead of stretching its row past 78px — tested with
  a deliberately long name in a real Chromium render and confirmed it reads
  as a clean, legible truncation (e.g. "1. Fussballclu…"), not a broken
  layout or a clipped-mid-glyph artifact. Real-browser verification (390px/
  412px/700px/1280px, not assumed) confirmed all three example rows render
  at an identical 78px height with no visible truncation for any of them
  (none needs the clamp to actually engage), and that the 481-959px/≥960px
  bands are unaffected. No change to REQ-214's fixed-cell-footprint
  guarantee — this only sizes the footprint itself, the same class of
  change S-049/S-055 already made at their own breakpoints.
- **Header nav breakpoint (added 2026-07-19, REQ-712):** the mobile
  hamburger toggle (SCREEN-07) activates below **480px**, reusing this
  section's existing narrow-phone value — the same one that already
  governs `Grid.css`'s header-label wrapping (the "Below a 480px viewport"
  bullet earlier in this section) — rather than the other candidate
  already in use elsewhere in the app, `.app`'s 960px desktop-cap
  breakpoint (S-040/S-047/S-049). 480px was chosen because it's the value
  this codebase already treats as "narrow phone" specifically, and the
  header-nav-overflow problem this requirement fixes is the same class of
  problem (content that reads fine at tablet/desktop widths overflowing at
  genuinely narrow phone widths) that value was already chosen for — reusing
  it keeps "narrow phone" meaning one consistent width across the app
  rather than acquiring a second, undocumented threshold. 960px was
  rejected: it demarcates "wide desktop gets more breathing room," an
  unrelated concern (more space, not overflow prevention), and using it
  here would collapse the nav behind a toggle on ordinary tablets and small
  laptops where the row already fits comfortably (verified: at 481px, the
  three-item row this requirement's own REQ-713 consolidation left behind —
  "Leaderboard," "Settings," "Log out," plus the "xG Arcade" title — totals
  well under 480px of required width using this document's own token
  values, so 481-959px was never actually part of the overflow problem
  being solved). Implementation is CSS-only (`HeaderNav.css`'s
  `@media (max-width: 480px)`), matching this section's existing
  "component-level breakpoints, not a JS viewport-detection layer"
  approach — the toggle and the plain row are the same DOM regardless of
  width; only which of them is visible changes.

## 5. Copy and voice

Unchanged from v0.1:

- Active voice, name the action: "Submit guess," "Join league," "Create league."
- Errors state what happened and what to do, without apologizing.
- Empty states are invitations: "You're not in any custom leagues yet" +
  a "Create league" button.
- ~~The live/final distinction is a voice rule as much as visual — always
  say "live" or "final."~~ **Removed (2026-07-14, doc-sync miss from
  S-041):** S-041 already dropped this distinction from the cell entirely
  (no more "live"/"final" text anywhere in SCREEN-01a) — this bullet
  should have been removed in that story's own doc-sync pass and wasn't,
  caught only now while fixing state 3/4's "no attempts left" wording for
  the same reason.

## 6. Accessibility and quality floor

- Flags and badges are always paired with a text label — never the sole
  identifier for a category, both for accessibility and because emoji flag
  rendering varies across platforms/fonts.
- Correct/incorrect and attempts-remaining are never color-only signals
  (points values and attempt-count text are always real text, never
  icon/color alone). Live vs. final is no longer a distinction the cell
  itself makes at all as of S-041 — see SCREEN-01a and SCREEN-06. "No
  attempts left" as a distinct text label is also gone as of 2026-07-14
  (SCREEN-01a state 3's note) — a locked-incorrect cell's points value
  alone now carries that meaning, same as a correct cell's points alone.
- Visible keyboard focus state using `accent-green` as the focus ring color.
- `prefers-reduced-motion` disables the badge-dock slide (§2), replacing it
  with an instant state change plus a brief color flash.
- Minimum 44×44px touch targets on all interactive elements.
- Sufficient contrast for gold-on-white and green-on-white text/icon use —
  **verified (S-013): both failed as originally specified (`accent-gold`
  2.6:1, `accent-green` 3.4:1 against `surface-card`); resolved via the new
  `accent-gold-text`/`accent-green-text` tokens in §2**, not by darkening
  the original tokens in place, since those remain correct for non-text/
  decorative use.

## 7. Open questions

- Whether a dark theme is ever offered as a user preference, now that light
  is the default (reversed from v0.1's "dark only" assumption)
- Whether the badge-dock animation is cheap enough in practice once built;
  if janky on low-end mobile, the reduced-motion fallback (instant + flash)
  may need to become the default rather than just the accessibility path
- (Phase 2) Fallback treatment when API-Football doesn't have a crest for a
  given club (lower-league/historical clubs) — likely the same generic
  initial-chip already used as v1's default, but not yet designed as an
  explicit "missing crest" state distinct from "v1 doesn't have crests at all"
- **No SCREEN-xx spec exists for the login/signup screen** (flagged by
  `ui-implementer` building S-010). Built functionally for Tier 0 —
  email/password fields, the REQ-701 age-confirmation checkbox, a "Log in"/
  "Sign up" tab toggle, tokens-only styling — but this document has no
  wireframe, copy, or state list for it the way SCREEN-01/01a/02 do. Needs a
  real SCREEN-00 entry (loading/submitting state, error copy, the exact
  tab/toggle pattern) rather than leaving the built version as the
  unreviewed de facto spec.
- **No SCREEN-xx spec exists for the post-login game-selection landing
  screen either** (`frontend/src/games/GameSelectScreen.tsx`, added S-021,
  REQ-303's UX addition). Same gap as SCREEN-00 above, same reasoning: kept
  deliberately minimal (a single tokens-only tile for xG Grid, no
  wireframe/copy/state review) since Tier 0 only ever has one game to
  select from — but once a second game exists this screen stops being
  trivial and needs a real spec (multi-tile layout, empty/loading states,
  copy) rather than staying an unreviewed de facto one.
- **§2 has no numeric spacing scale.** SCREEN-01/01a/02's implementation
  (S-010) used an unreviewed 4px-based scale (4/8/12/16/24/32/48) for
  padding/gaps in the absence of one, rather than one-off values per
  component. This should become a real token row in §2 (or be explicitly
  rejected in favor of per-component judgment) rather than staying an
  implementation-only convention future screens might diverge from.
- **§2 also has no type scale or border-radius scale**, a gap of the same
  kind as the spacing one above — found by `code-reviewer` on S-010's diff,
  since the first pass only disclosed the spacing gap. SCREEN-01/01a/02 and
  the login screen use ad-hoc, un-tokenized font sizes (9/10/11/12/13/14/
  15/16/18/22px, scattered across `CategoryLabel.css`, `CellState.css`,
  `Grid.css`, `GridScreen.css`, `GuessInput.css`, `AuthScreen.css`,
  `App.css`) and border-radius values (4/8/12px, in `Grid.css`,
  `GuessInput.css`, `AuthScreen.css`, `App.css`) with no shared variable
  behind either. Same recommendation as the spacing gap: turn these into
  real §2 token rows (a type scale, a radius scale) or explicitly decide
  per-component judgment is fine here — don't let it stay an
  implementation-only convention.
- ~~SCREEN-01a's revealed player name has no data source~~ — **fixed** the
  same session this was flagged: `GET /rounds/current`'s guess object now
  includes `SubmittedName` (REQ-303), so a cell answered before the current
  browser session can still show what was guessed after a reload. The
  client-side same-session cache (`GridScreen`'s `knownPlayerNames`) is kept
  only as the immediate-feedback path, since `POST .../guesses`' own
  response still doesn't echo the name back.
- ~~REQ-214's photo field name is still provisional~~ — **resolved**: the
  frontend's `resolvedPlayerPhotoUrl` guess (`CurrentRoundGuess`/
  `SubmitGuessResponse`, `frontend/src/lib/types.ts`) was checked against
  the backend's `ResolvedPlayerPhotoUrl` once it landed and matches
  exactly under the default camelCase JSON policy — no rename needed.
- ~~§2 has no overlay/scrim token for text-or-icon-on-photo contrast~~ —
  **resolved (2026-07-18, same session as the photo-decoupled-from-reveal
  status note; opacity lightened later the same day from 94% to 89% after
  visual feedback that 94% read as a heavy black shadow — see the
  `overlay-scrim` row above for the updated contrast math):**
  `overlay-scrim` (§2) is a band behind the checkmark/points/name overlay,
  calibrated against the worst case (a pure-white photo showing through)
  rather than a typical photo, at the lightest opacity that still clears
  the 4.5:1 floor for both overlaid foreground colors — pairs with
  `accent-gold` (not `accent-gold-text`) as the foreground color
  specifically on this token, the reverse of every other gold text/icon use
  in this document, since the darkened/lightened split is calibrated
  per-background-direction, not universally "always use the darkened one."
  `CellState.css`/`CellState.tsx` implement against this token directly —
  no bare `rgba()` value left untracked.
