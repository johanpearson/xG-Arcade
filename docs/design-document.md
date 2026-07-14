---
doc_id: design-document
title: UX & Design Document
version: "0.18"
status: draft
last_updated: 2026-07-14
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

Version 0.18 · 2026-07-14
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
| `accent-gold` | `#C99A2E` | Reserved for future non-text/decorative correct/locked-final use (e.g. a Phase 2 badge fill) — see `accent-gold-text` below for text/icon use, which is everywhere Tier 0 actually paints "correct" today |
| `accent-red` | `#C4463C` | Incorrect states — a muted brick red, not an alarm red. Passes text contrast as-is (~4.9:1 on white) — no separate text variant needed |
| `accent-green-text` | `#187E4F` | **(S-013)** Green text/icon labels, and white-on-green button-label backgrounds (`.guess-input__submit`, `.auth-screen__submit`) — `accent-green` itself measures ~3.4:1 against `surface-card`/white, below WCAG AA's 4.5:1 for normal text; this darkened variant measures ~5.1:1 |
| `accent-gold-text` | `#8D6C20` | **(S-013)** Correct/locked-final text and icons (`CellState`'s correct icon + meta line) — `accent-gold` itself measures ~2.6:1 against `surface-card`/white, failing even the 3:1 floor for large text/icons; this darkened variant measures ~4.9:1 |

Green means "live/active," gold means "settled/correct" — same semantic
split as before, just recolored for a light surface. This distinction is
load-bearing (REQ-205) so it must stay consistent everywhere. Flags and
badges bring in their own natural colors on top of this neutral shell —
the UI is deliberately quiet so those images read clearly, not muddied by
a busy background.

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
- An incorrect cell: red cross, attempts-left text (or "no attempts left"
  once locked) — SCREEN-01a states 2/3, unaffected by S-041.
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
At rest (default):
┌─────────────────────────┐
│                     ✓     │   ← gold checkmark — no dot, no "live" text,
│  12 pts                   │     no name until clicked/tapped
└─────────────────────────┘

Revealed (click/tap the cell — toggles closed again on a second click/tap):
┌─────────────────────────┐
│  Henry                ✓   │
│  12 pts                   │   ← unchanged at-rest line, stays visible
└─────────────────────────┘
```

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
(REQ-213), not repeated per cell. What the %-breakdown disclosure used to
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
│  no attempts left ·       │   ← guaranteed worst score (ADR-0021), stated
│  100 pts                  │      plainly, not implied
└─────────────────────────┘
      ↑ same rejected-guess cue plays here too, on the guess that used up
        the last attempt
```

**ADR-0021:** an incorrect/exhausted cell locks at `MaxPointsPerCell` (100
by default), not 0 — xG Arcade is scored like golf, so 0 is the *best*
possible score and must never be free just for guessing wrong.

**4. Round closed** (either prior state, now permanent):

```
Prior outcome: correct (at rest)      Prior outcome: incorrect
┌─────────────────────────┐           ┌─────────────────────────┐
│                     ✓     │           │                     ✕    │
│  88 pts                   │           │  final                   │
└─────────────────────────┘           └─────────────────────────┘
   ↑ gold checkmark — identical           ← no name here either,
     structure to state 1 at rest           same S-029 rule as states 2/3

Prior outcome: correct (revealed — click/tap the cell)
┌─────────────────────────┐
│  Henry                ✓   │
│  88 pts                   │   ← unchanged at-rest line, stays visible
└─────────────────────────┘
```

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
half of state 4 is unaffected by any of this — it already showed no name
(S-029) and still isn't a click target, since there's nothing to reveal.

"Attempt(s) left" and "no attempts left" still always appear as text, never
color/icon-only (REQ-204, accessibility) — that part of the original
accessibility rule survives S-041 unchanged, it's specifically the
live/final *distinction* that's gone, not the "never icon-only" principle
generally. State 1 and state 4 are now deliberately *not* visually
distinguishable at rest — see SCREEN-06 for where that distinction is
explained instead.

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
└───────────────────────────────┘
```

Unchanged from v0.1 structurally — tabs for Global vs. custom leagues, the
user's row always visually distinct. Recolored: the user's row uses
`surface-sunken` instead of a dark raised surface.

**ADR-0021 addition:** xG Arcade is scored like golf — lowest total wins,
the opposite of the natural "higher number = better" assumption most
players will bring from other games. The "Lowest total wins" line (plain
text, `text-muted` token, no new color) is added directly under the tab
row specifically to correct that assumption before a player reads any
rank — it must never be omitted or left implicit in the ranking order
alone. Rank #1 is always the lowest `TotalPoints`, consistent with
`LeaderboardService`'s ascending sort.

### SCREEN-04: Admin review (unverified data)

Still deliberately plainer/denser than the rest of the product — a working
tool, not a broadcast surface. On the light theme this now reads as a
clean, ordinary admin table rather than needing its own "un-dark" treatment.

```
┌─────────────────────────────────────────────┐
│ Unverified data (14)                          │
├─────────────────────────────────────────────┤
│ Henry · nationality · France · live_lookup    │
│   [Approve]  [Correct]  [Remove]              │
│ ...                                            │
└─────────────────────────────────────────────┘
```

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
Tier 0). No bare confirmation checkbox: the "current password" field is the
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
│ A correct cell shows a live    │
│ estimate that can still change │
│ until the round closes.        │
│                                 │
│ Once the round closes, that    │
│ value is locked and won't      │
│ change again.                  │
│                                 │
│ xG Arcade scores like golf —   │
│ lower is better. An answer     │
│ fewer other players also       │
│ guessed scores better than a   │
│ common one.                    │
└───────────────────────────────┘
```

**S-041, REQ-213.** Opened from the `(ⓘ)` entry point in SCREEN-01's
header, next to the round timer — a modal (`role="dialog"`,
`aria-modal="true"`), same pattern SCREEN-02's `GuessInput` already
established (backdrop click or `[×]`/Escape closes it, focus returns to the
entry point on close). Deliberately not a full route/screen: it's a short,
general explanation, never gated behind having attempted any cell, and
reachable at any time an active round is showing. Content is general to the
mechanic, never cell-specific (no "your cell scored 12 pts" — that number
already lives on the cell itself); the three paragraphs above are the
minimum required content (REQ-213): what a live estimate means and that it
can change, what a locked/final value means once the round closes, and —
in general terms, no exact formula — the golf-style/fewer-others-guessed
framing already established in SCREEN-03 (ADR-0021). Opening it must not
discard any in-progress state elsewhere on the screen (e.g. a filled-but-
not-yet-submitted guess in an open `GuessInput` sheet) — it's an overlay on
top of the grid screen, not a navigation away from it.

This is where SCREEN-01a's now-removed per-cell disclosure content (the
%-breakdown line, "updates until round closes on [date/time]") effectively
moved — see that section's S-041 note — except reworded to be general
rather than tied to one cell's specific numbers.

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

## 5. Copy and voice

Unchanged from v0.1:

- Active voice, name the action: "Submit guess," "Join league," "Create league."
- Errors state what happened and what to do, without apologizing.
- Empty states are invitations: "You're not in any custom leagues yet" +
  a "Create league" button.
- The live/final distinction is a voice rule as much as visual — always
  say "live" or "final."

## 6. Accessibility and quality floor

- Flags and badges are always paired with a text label — never the sole
  identifier for a category, both for accessibility and because emoji flag
  rendering varies across platforms/fonts.
- Correct/incorrect and attempts-remaining are never color-only signals
  (points/attempt-count values and "no attempts left" are always real text).
  Live vs. final is no longer a distinction the cell itself makes at all as
  of S-041 — see SCREEN-01a and SCREEN-06.
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
