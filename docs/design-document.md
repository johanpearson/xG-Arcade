---
doc_id: design-document
title: UX & Design Document
version: "0.7"
status: draft
last_updated: 2026-07-10
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

Version 0.5 · 2026-07-05
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
| `accent-green` | `#1E9E63` | Live/active states, primary actions — a clean pitch green, crisp rather than dark/muted |
| `accent-gold` | `#C99A2E` | Correct/locked-final states — a medal-gold, distinct from the green so live-vs-final stays unambiguous at a glance |
| `accent-red` | `#C4463C` | Incorrect states — a muted brick red, not an alarm red |

Green means "live/active," gold means "settled/correct" — same semantic
split as before, just recolored for a light surface. This distinction is
load-bearing (REQ-205) so it must stay consistent everywhere. Flags and
badges bring in their own natural colors on top of this neutral shell —
the UI is deliberately quiet so those images read clearly, not muddied by
a busy background.

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

**Signature element: badge dock.** When a guess is confirmed, the row's
flag/badge and the column's badge slide inward from either side and settle
next to the revealed player name — a small, literal "match" animation tied
directly to the game's actual mechanic (combining two categories), not a
borrowed broadcast trope. This replaces v0.1's split-flap animation, which
was a retro-broadcast flourish that didn't fit a clean, light direction.
Used only at guess-submit and round-close reveal, nowhere else. Respects
`prefers-reduced-motion`: badges appear already docked, no slide, with a
brief background color flash (green→gold) instead.

## 3. Key screens

### SCREEN-01: Grid (home)

```
Mobile (single column)                Desktop (grid + side panel)
┌─────────────────────────┐           ┌───────────────────────────────────┐
│ Round #14      ⏱ 1d 4h  │           │ Round #14      ⏱ 1d 4h  [Leagues▾]│
├─────────────────────────┤           ├───────────────────┬───────────────┤
│      [AFC] [MIL] [BAY]  │           │                    │  Your progress │
│ 🇫🇷 │ Henry│  +  │  +  │           │   3x3 / NxN grid   │  2/9 answered  │
│ 🇧🇷 │  +  │ Kaká │  +  │           │   (same as left)   │                │
│ 🇪🇸 │  +  │  ✕  │  +  │           │                    │  Live 12%       │
│                          │           │                    │                │
│ Total: 69 pts            │           └────────────────────┴───────────────┘
└─────────────────────────┘
```

- Row headers: flag + country name when the row category is a nationality;
  a club badge + club name when the row category is a club (REQ-107 means
  a grid is always Club×Club or Club×Country, never Country×Country, so at
  most one axis is ever flags — the other is always badges).
  Column headers follow the same rule for whichever axis they represent.
- An empty cell shows a faint "+" with no imagery — imagery only appears
  once a cell has an answer, so an unanswered grid doesn't feel cluttered.
- A live cell: player name, small green dot (not a heavy pulse — a quiet
  4px dot with a slow, subtle opacity shift), live `unique_percent` in mono
  numerals, "updates until round closes" microcopy (REQ-204).
- A locked cell: gold checkmark (correct) or red cross (incorrect), no
  motion, `FinalPoints` shown (REQ-205).
- Desktop's side panel is additive only — mobile gets the same information
  stacked below the grid.

### SCREEN-01a: Cell states (component, appears within cells)

Four distinct states now exist per REQ-210, not two — correctness is
revealed immediately (REQ-203), separate from whether the round has closed:

**1. Correct, round still active** (locked from further guessing, score
still live until round close):

```
┌─────────────────────────┐
│  Henry            ✓ live │   ← gold check (correct) + small green dot
│  12% unique               │      (still live) — both shown together,
│  updates until 18:00 Fri  │      since "correct" and "final" are different
└─────────────────────────┘      moments (REQ-203)
```

**2. Incorrect, one attempt remaining:**

```
┌─────────────────────────┐
│  Ronaldinho        ✕      │   ← red cross, not locked
│  1 attempt left           │   ← always spelled out, never just an icon
└─────────────────────────┘
```

**3. Incorrect, no attempts remaining** (round still active, cell is done):

```
┌─────────────────────────┐
│  Ronaldinho        ✕      │
│  no attempts left · 0 pts │   ← guaranteed 0, stated plainly, not implied
└─────────────────────────┘
```

**4. Round closed** (either prior state, now permanent):

```
┌─────────────────────────┐
│  Henry              ✓    │   ← gold checkmark, static, no "live" dot at all
│  12% unique · 88 pts     │
│  final                   │
└─────────────────────────┘
```

"Live," "final," "attempt(s) left," and "no attempts left" always appear as
text, never color/icon-only (REQ-204, accessibility). State 1 vs. state 4
is the one place two "positive" indicators (check + dot, or check alone)
need to stay visually distinguishable at a glance — the dot's presence is
what signals "still updating," not the checkmark, which appears in both.

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
├───────────────────────────────┤
│ 1  Alex        142 pts         │
│ 2  You         138 pts   ← you │
│ 3  Sam         120 pts         │
└───────────────────────────────┘
```

Unchanged from v0.1 structurally — tabs for Global vs. custom leagues, the
user's row always visually distinct. Recolored: the user's row uses
`surface-sunken` instead of a dark raised surface.

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

## 4. Responsive strategy

Unchanged from v0.1 — built "equally both" from the start:

- Layout defined per breakpoint at the component level, not one fluid
  layout reflowing.
- Grid cell minimum touch target: 44×44px on mobile regardless of grid
  size; a 5x5 on a narrow phone scrolls horizontally with sticky row/column
  headers rather than shrinking below that floor.

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
- Live/final and correct/incorrect are never color-only signals.
- Visible keyboard focus state using `accent-green` as the focus ring color.
- `prefers-reduced-motion` disables the badge-dock slide (§2), replacing it
  with an instant state change plus a brief color flash.
- Minimum 44×44px touch targets on all interactive elements.
- Sufficient contrast for gold-on-white and green-on-white text/icon use —
  verify actual contrast ratios once real components exist, since gold in
  particular can run light-on-light if not deliberately darkened for text use.

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
