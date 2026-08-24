---
doc_id: design-document
title: UX & Design Document
version: "0.79"
status: draft
last_updated: 2026-08-24
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

# UX & Design Document – xG Arcade

Version 0.40 · 2026-07-20
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

**Imagery note:** flags are rendered as small bundled SVGs (simplified flat
bands/crosses/a plain circle per country — no coats of arms, stars, or other
fine detail), safe and license-free the same way the original Unicode-emoji
approach was. **Changed 2026-08-03 (user-tester bug report)**: v1 originally
shipped flags as literal Unicode flag emoji; that degrades badly on Windows,
where Chrome/Edge render emoji through the host OS font and Windows dropped
color flag glyphs from its system font, so a flag emoji fell back to its two
bare Regional Indicator Symbol letters (e.g. "GB") with no flag graphic at
all — Firefox alone avoided this because it bundles its own emoji font
(Twemoji Mozilla) rather than asking the OS to render the glyph. The bundled-
SVG approach needs no host font support, so it renders identically on every
platform/browser. See `frontend/src/lib/countryFlags.tsx`'s own top-of-file
comment for the full reasoning. Club crests are **deferred
to Phase 2** — v1 ships with the placeholder circular initial-badges shown
throughout this document as the actual design, not a temporary stand-in.
Real crest sourcing via API-Football (`ClubCrest` caching, see
`implementation-document.md` and ADR-0007/0008) is designed and ready to
build, but intentionally not part of v1 scope — see
`requirements-document.md` §6. When it does ship, the initial-badge becomes
the fallback for any club without a crest on file, not something removed
entirely.

## 2. Token system

**Color** (light theme is the default and, as of v1's actual shipped UI,
the only theme rendered in the app — see the **Dark theme** subsection
below §2's light-theme table for a fully specified, contrast-verified dark
counterpart, decided 2026-07-20 as REQ-716's design pass. Implementation
is not yet built; this table remains the light-theme spec, unchanged):

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

**Dark theme (REQ-716, decided 2026-07-20 — token values only, not yet
implemented):** resolves §7's former open question ("whether a dark theme
is ever offered"). **This is a fresh derivation, not a revival of v0.1's
rejected dark tokens** (see this document's revision note at the top) — v0.1
was rejected for reading as a generic "dark-mode-analytics-dashboard" that
had nothing to do with football specifically; that critique was about the
app's *default* identity, not about whether a dark theme is ever offered as
an *option*. Every value below is independently derived and contrast-checked
against this dark surface set, not copied from v0.1.

*Mechanism decision (resolves the persistence/toggle question §7 also left
open — see ADR-0034 for the full alternatives-considered record):* an
**explicit toggle in Settings (`SettingsScreen.tsx`, SCREEN-08),
not an automatic-only `prefers-color-scheme` switch.** Three states —
**System** (default), **Light**, **Dark** — persisted in `localStorage`
under a new key (device-local, same pattern as ADR-0033's refresh-token
storage: no `User`-level/account-synced preference for v1, no new backend
surface). "System" reads `prefers-color-scheme` at load and reactively via
its `change` event; "Light"/"Dark" pin the theme regardless of the OS
setting. Reasoning:
- REQ-716's own request text is explicit — *"I want to **choose** a
  different color theme... to match my own preference"* — a
  system-preference-only approach answers "does the app support dark mode"
  but not "can I choose it independent of my OS setting," which is what was
  actually asked. An automatic-only approach was considered and rejected on
  this basis, not on complexity grounds.
- Simplicity is still respected within that constraint: this is Tier 0 and
  a solo/small-scale product (`MVP-SCOPE.md`), so the decision stops at the
  minimum that satisfies the actual request — one `localStorage` key, no
  account-level sync across devices (a real gap if a player switches
  devices, but not worth a backend change for a preference this low-stakes;
  revisit only if actually requested), no per-user theme authoring beyond
  the two themes already designed here.
- `localStorage` over a `User`-level column: mirrors ADR-0033's own
  reasoning almost exactly (device-local, no new backend/API surface, one
  storage mechanism already understood in this codebase) — the trade-off
  there was XSS blast radius for a security-sensitive token; here the
  trade-off is just "doesn't follow the player to a new device," a far
  lower stake for a cosmetic preference. Implementation should read this
  key the same way `App.tsx` already reads `ACCESS_TOKEN_STORAGE_KEY` at
  startup, applying the resolved theme (System-resolved-to-Light/Dark, or
  the explicit pin) as a `data-theme` attribute (or class) on `<html>`
  before first paint, to avoid a flash of the wrong theme.
- Defaulting to "System" (rather than defaulting to "Light" and requiring
  an opt-in click) means a player who has never opened Settings still gets
  a sensible, lighting-condition-appropriate result — the toggle exists for
  the player who wants to *override* that, not as the only way to get a
  dark UI at all.

*Token values.* Colors only — every value below is a straight color-token
substitution. **Layout, spacing (`--space-*`), typography (type scale,
`--font-*`), and animation (badge-dock slide, rejected-guess shake) are
unchanged and theme-independent** — nothing in §3/§4's spacing or motion
specs needs re-reading for dark theme; only the hex/`rgba()` values below
differ.

| Token | Dark value | Notes / contrast |
|---|---|---|
| `bg-base` | `#101412` | App background — near-black, same quiet green undertone (low-saturation, ~152° hue family) as `text-primary`'s own undertone in the light theme, inverted in lightness rather than a neutral/blue-black default |
| `surface-card` | `#1C211F` | Cards/cells — lighter than `bg-base` so cards still "lift" without a shadow, mirroring the light theme's `surface-card` (white) being lighter than its `bg-base` (off-white) |
| `surface-sunken` | `#0C0E0D` | Empty/inactive cells, input backgrounds — darkest of the three neutral surfaces, same relative ordering as the light theme (there, `surface-sunken` is also the darkest of the three: `surface-card` > `bg-base` > `surface-sunken` by luminance; here: `surface-card` > `bg-base` > `surface-sunken` too) |
| `text-primary` | `#EEF1F0` | Primary text — near-white, same green undertone as its light-theme counterpart, inverted. **16.3:1** against `bg-base`, **14.4:1** against `surface-card` (WCAG relative-luminance formula) — both far above the 4.5:1 floor, consistent with the light theme's own near-black-on-near-white pairing also clearing it by a wide margin |
| `text-muted` | `#98A49E` | Secondary text/labels. **6.3:1** against `surface-card` (the lighter, more binding of the two real backgrounds this token sits on), **7.2:1** against `bg-base` — clears 4.5:1 with real margin, same role as the light theme's `text-muted` (which was tuned close to the floor, ~4.6:1, against `bg-base`; more margin was taken here deliberately since there's no reason to shave it thin) |
| `border-hairline` | `#353B38` | Dividers/card borders — same "thin, quiet" decorative role as the light theme's hairline, which itself was never given a hard contrast floor (measured ~1.2:1 against `bg-base` there, i.e. deliberately subtle, not a WCAG-bound UI-component boundary); this dark value is subtle by the same design intent, not by a computed floor |
| `accent-green` | `#1E9E63` | **Unchanged from the light theme — same hex.** Non-text/decorative use (live-dot, focus ring, tab underline), same role as light theme. **New finding for dark theme, verified rather than assumed:** this same value also now clears the text/icon contrast floor when used as *text* on a dark surface — **4.76:1** against `surface-card`, **5.42:1** against `bg-base` — because the direction of the light/dark split reverses on a dark background, the same phenomenon already documented for `overlay-scrim`'s gold pairing. Consequently: |
| `accent-green-text` | *(not used in dark theme)* | The light theme's darkened variant is **dormant in dark theme, not carried over** — verified to fail on dark surfaces (**3.21:1** against `surface-card`, below 4.5:1) precisely because darkening a color lowers its luminance, which helps contrast against a *light* background and hurts it against a *dark* one. Dark theme uses `accent-green` directly (row above) for every role `accent-green-text` covers in the light theme (button labels, correct-adjacent green text) — one token instead of two, a genuine simplification, not an oversight |
| `accent-gold` | `#C99A2E` | **Unchanged from the light theme — same hex**, same reasoning as `accent-green` above: this value clears text/icon contrast on a dark surface — **6.33:1** against `surface-card` — so it serves both the decorative and text/icon roles in dark theme |
| `accent-gold-text` | *(not used in dark theme)* | Dormant in dark theme, same reasoning as `accent-green-text` above — verified to fail (**3.34:1** against `surface-card`) since it's a darkened-for-light-backgrounds variant |
| `accent-red` | `#D2726A` | **New value — the light theme's `accent-red` (`#C4463C`) fails on a dark surface** (**3.33:1** against `surface-card`, below 4.5:1) since it's a mid-lightness color calibrated to pass against white, and unlike gold/green there was no existing brighter sibling token to fall back to. This is the same hue/saturation as `accent-red` (H≈4°, S≈54%), lightness raised from 50% to 62% — **4.94:1** against `surface-card`, **5.62:1** against `bg-base` — a genuinely new token needed for dark theme, not a reuse |
| `overlay-scrim` / `accent-green-scrim` (photo-overlay set, plus `accent-gold`/`surface-card` used as their foreground pairing) | **No change, either theme.** | These are calibrated against a *photo's* own worst-case brightness (a pure-white photo showing through the scrim — see each token's own row above), not against the app's chrome background — a real player photo's brightness has no relationship to which theme the surrounding UI is in. The existing verification (89% opacity scrim, `accent-gold`/`accent-green-scrim`/`surface-card` foreground pairings) applies unchanged in dark theme; do not re-derive or theme-split these |

**Contrast methodology note:** every ratio above uses the same WCAG
relative-luminance formula this document already applies elsewhere (see
`overlay-scrim`/`accent-green-scrim`'s derivations above) — `surface-card`
was used as the binding/worst-case background for each text-on-surface
pairing where a token could plausibly sit on either `bg-base` or
`surface-card`, since it's the lighter (lower-contrast-margin) of the two
dark-theme surfaces, mirroring how the light theme's own ratios are
computed against `surface-card`/white as the binding case there.

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

**Round-completion settle-in (REQ-1210, ADR-0083, SCREEN-12).** When a
player finishes a round of any game (xG Grid or xG Path today), the
completion banner enters with the same fade-plus-rise "settle" character
already established elsewhere in this app (the badge dock's own arrival
above, and SCREEN-10's clue-node reveal) — a brief upward slide combined
with a fade from transparent, never a bounce, spin, or anything more
attention-grabbing than those two precedents. Deliberately reuses that
existing motion character rather than introducing a new signature
animation: this is a generic, cross-game moment (ADR-0083), not a
game-specific flourish, so it shouldn't visually compete with either
game's own signature motion (badge dock for xG Grid, clue-node reveal for
xG Path). Fires once, on the in-session moment of completing a round
(never automatically on loading or revisiting an already-finished round —
see SCREEN-12's own note on REQ-1210 §7's open "replay?" question).
Respects `prefers-reduced-motion`: the banner, its points value, and its
leaderboard link all still appear immediately, with the motion itself
(not the content) removed — same fallback pattern as the badge dock and
rejected-guess cue above, and REQ-1210's own explicit acceptance
criterion that neither the points value nor the link may ever be gated
behind the animation actually playing.

**Brand mark (2026-07-26, revised same day).** A small icon/logo pair
replaces the plain "xG Arcade" text on `SplashScreen` (REQ-719 shipped
without one, "to be handled separately" — this is that follow-up): an "xG"
monogram on a rounded-square badge — xG (expected goals) is the term the
whole product name is built on, so it's the mark's entire content, not a
supporting detail beside a separate pictorial symbol. **First attempt used a
2x2-grid glyph instead of the monogram; direct feedback the same day asked
for xG itself to be the visual center, not a grid icon — the grid version
was replaced outright, not kept as an alternate.** Implemented as
`frontend/src/splash/Logo.tsx` (`LogoMark` is the badge alone, `Logo` pairs
it with the word "Arcade" — not the full "xG Arcade" repeated next to its
own monogram) and, as a static asset, `frontend/public/favicon.svg`. Colors
are fixed rather than theme-driven: `accent-green` is already the same hex
across light/dark theme (see this section's dark-theme table), and the
monogram text is a literal white for the same "self-contained badge, not
page chrome" reasoning already applied to `overlay-scrim`'s foreground
pairings above — so the mark needs no dark-mode variant of its own. The
"Arcade" word next to it still uses `--font-display`/`text-primary` as
normal, so it adapts with theme like any other heading text. No new token
was added.

**2026-07-26, same-day extension:** `Logo` moved from `frontend/src/splash/`
to `frontend/src/components/Logo.tsx` (a genuine second consumer now
existed, not a speculative move) and replaced the header's own plain-text
"xG Arcade" title in `App.tsx` (both the authenticated button and
unauthenticated `<h1>` variant). Same mark, same accessible-name mechanism,
so every existing `getByRole('button'|'heading', { name: 'xG Arcade' })`
query kept passing with no test changes.

**2026-07-26, second same-day revision — user-supplied inspiration.** The
user shared reference logos (bold two-tone "xG," a soccer ball worked into
the lettering, a motion-swoosh trail, gradient shading, a tagline pill) and
asked for xG itself to be more the visual center. Adopted selectively rather
than wholesale — see the direction question this was resolved with:
- **Adopted, because it fits the existing flat/token system:** two-tone
  letters (`x` in `accent-green`, `G` in `accent-gold-text`) and a flat
  (no gradient/shading) ball glyph — a plain circle with one pentagon,
  not a textured illustration — tucked against the G.
- **Not adopted, because it conflicts with §1's own settled direction:**
  the gradient shading, motion-swoosh trail, and dissolving-pixel effect.
  §1 already rejected a "broadcast-graphics" look in favor of flat and
  quiet; §2 defines no gradient tokens at all. Revisit only via a real
  token-system update, not as an icon-only exception.
- `Logo` (the in-app lockup — `SplashScreen`'s `<h1>`, `App.tsx`'s header)
  is now badge-less: "x", "G", and "Arcade" are plain text sitting directly
  on `bg-base`, using `accent-gold-text` (not raw `accent-gold`) for the G
  specifically, since design-document.md §2 already measured raw
  `accent-gold` too low-contrast for text/icon use on a light surface —
  `accent-gold-text` already resolves to the right per-theme value via
  index.css's existing `data-theme` override, the same mechanism
  `SettingsScreen`/`CellState` already rely on, so no new CSS was needed
  for the dark-theme case.
- `LogoMark` (the self-contained icon — `favicon.svg` and any future
  app-icon use) deliberately did **not** switch to two-tone letters: raw
  `accent-gold` measures too close in lightness to `accent-green` to read
  reliably as G-on-green at small icon sizes, so it keeps the original
  white-on-green monogram, with the same flat ball glyph (inverted
  fill — white circle, green pentagon, so it reads against the green
  badge) added as a corner accent for visual continuity with `Logo`.
- **Accessible-name implementation note, worth recording because it's easy
  to get wrong again:** splitting "x" and "G" into separate sibling
  elements (needed for independent coloring) changes the computed
  accessible name from "xG" to "x G" — the accessible-name algorithm
  inserts a joiner space between *each child element's own contribution*
  when accumulating a parent's name, not just between literal whitespace
  in the markup. Fixed with `aria-label="xG"` on the wrapping span so it
  contributes as one atomic string. A second, unrelated gotcha found the
  same day: a flex container (`.logo` is `display: inline-flex`) ignores a
  whitespace-only text-node child for *layout* purposes even though it's
  still read for the *accessible name* — so the literal space kept between
  the "xG" span and "Arcade" (for the name) rendered with zero visual
  width, and the visible gap had to come from `gap` on `.logo` instead.

**2026-07-26, third same-day revision — ball accent dropped.** Direct
feedback after seeing the ball glyph live: "too much" and didn't look
good. Removed outright (`.logo__ball`/`BallAccent` in `Logo.tsx`, and the
matching corner glyph in `LogoMark`/`favicon.svg`), not kept as a toggle —
the two-tone "xG" letters alone were already reading well and are what
remains. `Logo` is back to just `x`/`G`/`Arcade` as plain sibling text
(still needing the same `aria-label="xG"` wrapper for the accessible-name
reason above, since that's independent of whether a ball accent exists);
`LogoMark`/`favicon.svg` are back to the plain white-on-green monogram
with no corner glyph.

**Placeholder avatar (REQ-216, 2026-08-03 amendment).** A new, generic
graphic — flagged as a gap by `requirements-document.md`'s REQ-216 (the
"2026-08-03 status note" amending its original no-photo-fallback wording)
and added here, per CLAUDE.md's "Frontend visual consistency" rule, before
`ui-implementer` wrote any code against it. Shown on a locked, incorrect
cell (SCREEN-01a states 3/4's incorrect branch) in place of a real photo
whenever one isn't available — either the guess matched no
`PlayerNameIndex` candidate at all, or it matched a real player but
ADR-0057's Wikidata-only lookup didn't resolve a photo (timeout, error, or
genuinely no image). Never shown on a *correct* cell — REQ-214's own
no-photo fallback there stays genuinely nothing, an intentional asymmetry
recorded (not re-litigated) in REQ-216's own status note.

- **Shape:** a flat, single-tone generic person silhouette (a circle for
  the head, a simple rounded shoulder shape beneath it) — no gradient,
  texture, or literal likeness, consistent with §1's flat/quiet direction
  and the same "no textured illustration" restraint the brand mark's ball
  glyph note above already applies. It is deliberately generic/anonymous:
  this graphic never implies a specific player, real or fictional — it
  means "no confirmed image," nothing more.
- **Color tokens — both reused, no new color added:** the silhouette
  itself uses `text-muted` (the same "quiet, secondary content" role that
  token already carries everywhere else in this table — a placeholder
  glyph is exactly that, not a status signal); its containing slot's
  background uses `surface-sunken` (the same "recessed/inactive" role that
  token already documents for empty/inactive cells and input backgrounds —
  a placeholder avatar's backdrop is conceptually the same "nothing here
  yet" recess). Deliberately **not** `accent-red`: the locked-incorrect
  cell's persistent red border (below) already carries the "this is
  wrong" signal on its own — painting the avatar itself red as well would
  make the avatar read as a second, redundant incorrect-cue rather than
  the neutral "no image" cue it's meant to be, and would need its own
  fresh contrast derivation the way `accent-green-scrim` needed one for
  its own narrow exception, which nothing about this graphic warrants.
- **Footprint:** fills the cell's whole box using the exact same full-bleed
  mechanism REQ-214's photo already established — absolutely positioned
  against `.grid-table__cell` (the `<td>`), `inset: 0`, so it can never
  grow or shrink the cell regardless of viewport, matching every other
  state's fixed-footprint guarantee. See `CellState.css`'s
  `.cell-state--incorrect-photo` rule (shared with `.cell-state--photo`
  where the properties are identical) for the implementation.
- **Accessibility:** decorative only, same pairing rule §6 already applies
  to every other glyph in this file (flag/badge glyphs, the correct/
  incorrect check/cross icons) — the graphic itself carries no accessible
  name of its own; the cell's own accessible text (its aria-label, or the
  guessed player's name when one is shown alongside it) is what a screen
  reader actually announces.
- **Persistent incorrect-cell border, extended (2026-08-03):** the
  correct-cell-only persistent border this section documents further below
  (SCREEN-01a's "Persistent correct-cell border" note) is joined by a
  matching persistent `accent-red` border on a locked-incorrect cell
  (states 3/4's incorrect branch) — REQ-216's own acceptance criteria
  requires a red border on all three of its combinations, including the
  "no match at all" case that previously had no border at all. Same
  mechanism, same element (`.grid-table__cell`, not `.grid-cell` or
  anything inside `CellState.tsx`), same reasoning (a full-bleed photo/
  placeholder layer can now appear on an incorrect cell too, exactly the
  scenario the correct-cell border was already built to survive
  regardless of stacking order) — see SCREEN-01a's own status note for the
  full detail and Grid.css's `.grid-table__cell--incorrect` rule.

## 3. Key screens

### SCREEN-01: Grid (home)

```
Mobile (single column)                Desktop (grid + side panel)
┌─────────────────────────┐           ┌───────────────────────────────────┐
│ Ends in 1d 4h  (ⓘ)      │           │ Ends in 1d 4h  (ⓘ)   [Leagues▾]   │
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

**2026-07-21 correction (REQ-303's dated addition):** the mock above was
corrected to match what was actually built, per that addition's acceptance
criteria. The end-time indicator's visible text is the relative-duration
string itself with an explicit `"Ends in "` prefix (e.g. `"Ends in 1d
4h"`, or the fixed `"Ending soon"` fallback within 60 seconds of — or
past — `endTime`) — not a bare clock-icon-plus-duration (`⏱ 1d 4h`) as the
mock previously showed; there is no clock icon. The mock's earlier `Round
#14` label has also been dropped: at the time of this correction, no field
in `GET /rounds/current`'s response carried a human-friendly round number,
only an opaque `roundId`, so showing one would have implied a capability
that didn't exist.

**Update (2026-08-17, REQ-304/S-135):** `GET /rounds/current`'s
`CurrentRoundResponse` now does carry a human-friendly `sequenceNumber`
field (added for the admin-only round-control label, see SCREEN-04's own
status note) — so the field-doesn't-exist reasoning above no longer holds.
This screen still does not render it: REQ-304's own acceptance criteria
scope the visible "Grid Round #N"/"Path Round #N" label to the admin
round-control section only, not this player-facing grid header. Whether to
also surface it here remains a separate, not-yet-scoped product decision,
not something to infer from either correction.

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

**REQ-216 status note (2026-08-03, direct product-owner sign-off — narrowly
supersedes the S-029 "no name shown" rule above, but only for states 3/4's
incorrect branch, never state 2):** a cell that locks with its final guess
still incorrect now shows some feedback about who was actually guessed,
instead of a bare ✕. Three combinations, all sharing the same full-bleed
"photo slot" mechanism REQ-214's own photo cell already established
(`CellState.css`'s `.cell-state--incorrect-photo`, reusing the identical
positioning rule `.cell-state--photo` uses) — a red border (§2's persistent-
border note, extended) is common to all three:

```
(1) Guess matched a real player, photo resolved (ADR-0057)
┌─────────────────────────┐
│▒▒▒▒▒▒[ matched player's ▒│   ← red border around the whole cell
│▒▒▒▒▒▒ photo, fills cell]▒│
│▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│
│▓ Seedorf                  │   ← canonical name, always shown here
│▓ 100 pts                  │
└─────────────────────────┘

(2) Guess matched a real player, no photo resolved (timeout/error/no image)
┌─────────────────────────┐
│▒▒▒▒[ placeholder avatar ▒│   ← red border; §2's new "Placeholder avatar"
│▒▒▒▒  — muted silhouette]▒│     graphic in place of the photo
│▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│
│▓ Seedorf                  │   ← name still shown — only the photo failed
│▓ 100 pts                  │
└─────────────────────────┘

(3) Guess matched no PlayerNameIndex candidate at all (typo/gibberish)
┌─────────────────────────┐
│▒▒▒▒[ placeholder avatar ▒│   ← red border; same graphic as (2)
│▒▒▒▒  — muted silhouette]▒│
│▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│
│▓ 100 pts                  │   ← no name — nothing resolved to a real
└─────────────────────────┘        player, so none is shown
```

Never shown for state 2 (an attempt remains) — that state is completely
unaffected by this note, still exactly the plain "✕ + N attempt(s) left"
mock above, no image, no name, no border. No checkmark/cross icon is
rendered in any of the three combinations above either — mirroring REQ-214/
S-048's own established "the photo overlay shows only name + points, never
a status glyph" pattern; the red border is what signals "incorrect" here
instead, the same way the green border now signals "correct" for a photo
cell that has none of its own status glyph either. **Asymmetry, recorded
plainly rather than resolved (see `requirements-document.md`'s REQ-216
status note for the full reasoning):** this is a direct, deliberate
inconsistency with REQ-214's own no-photo fallback for a *correct* cell
(shows nothing at all) — this REQ's own no-photo fallback is the
placeholder avatar, never nothing.

**4. Round closed** (either prior state, now permanent):

```
Prior outcome: correct (at rest)      Prior outcome: incorrect (typo/no match
┌─────────────────────────┐           — see REQ-216's combination (3) above
│                     ✓     │           for the matched/real-player variants)
│  88 pts                   │           ┌─────────────────────────┐
└─────────────────────────┘           │▒▒▒▒[ placeholder avatar ▒│
   ↑ gold checkmark — identical        │▒▒▒▒  — muted silhouette]▒│
     structure to state 1 at rest      │▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓│
                                        │▓ 100 pts                  │
                                        └─────────────────────────┘
                                           ↑ red border, no name — same
                                             REQ-216 combination (3) mock
                                             above; state 4's incorrect
                                             branch renders identically to
                                             state 3, same MaxPointsPerCell
                                             value regardless of when the
                                             cell locked (unchanged from
                                             before REQ-216)

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

**Persistent correct-cell border (2026-08-03, direct product feedback):** a
correct cell (states 1 and 4 above, no-photo or photo variant alike) now
also gets a `--color-accent-green` border, 2px, around the whole cell —
an always-visible cue that a cell is correct, independent of and in
addition to the checkmark/points text tint (`--color-accent-gold-text`)
this section already describes. Before this addition, "correct" was only
ever signaled by the checkmark glyph and the gold-tinted points text — no
border existed at all. `--color-accent-green`, not `--color-accent-green-scrim`
(§2's dormant, one-off checkmark exception, not a general "correct"
color), is the right token: it's already specified for exactly this kind
of non-text/decorative use (live-dot, focus ring, tab underline), already
measures ~3.4:1 against `surface-card`/white — clearing the 3:1 floor that
applies to a decorative UI-component border, not the 4.5:1 text floor —
and is unchanged between light/dark theme (§2's dark-theme table), so no
new theme-specific value was needed. Implemented on `.grid-table__cell`
(the `<td>` itself, `Grid.tsx`/`Grid.css`), not `.grid-cell` (the button)
or anything inside `CellState.tsx`: a photo cell's photo layer bleeds only
as far as this element's own padding edge (see this section's own S-050
status note), never into its border area, so a border declared on the
`<td>` is spatially guaranteed to render around/above the photo in both
variants, without depending on paint-order/stacking-context specifics the
way a border on `.grid-cell` would.

**Extended to a locked-incorrect cell too (REQ-216, 2026-08-03):** the same
`.grid-table__cell`-not-`.grid-cell` reasoning above now also applies to a
locked-incorrect cell (states 3/4's incorrect branch) — see this state's
own REQ-216 status note above for the three combinations this covers.
`.grid-table__cell--incorrect` (Grid.tsx/Grid.css) gives that element a
`--color-accent-red` border, same 2px weight, same non-text 3:1 floor
reasoning (`accent-red` already measures ~4.9:1 against `surface-card`/
white, clearing it with real margin) — added specifically because REQ-216
can now put a full-bleed photo or placeholder-avatar layer on an incorrect
cell too, the exact scenario this element (rather than `.grid-cell` or
`CellState.tsx`) was already chosen to survive regardless of stacking
order. Before REQ-216, an incorrect cell (locked or not) had no border at
all — this is a genuinely new cue for the locked case specifically, not
carried over from anywhere. Still never applied to state 2 (an attempt
remains) or an unattempted cell — only once locked-incorrect.

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
against (ADR-0007) carries `name`/`birthYear` only, no photo field, so each
suggestion row instead shows the name plus an optional `birthYear` caption
line in `text-muted` for disambiguation. Avatar support stays an open item
if/when the index gains a photo field.

**Nationality removed from the autocomplete contract (post-S-032 fix):**
suggestion rows originally also carried `nationality`, shown alongside
`birthYear` in the same caption line. That leaked the answer for
nationality-based categories (e.g. Country × Club) — seeing which
suggestions carried the target nationality told the player who was
eligible before they'd even guessed, violating REQ-207/ADR-0007's "implies
nothing about correctness" rule. `nationality` was removed entirely from
`GET /players/autocomplete`'s response and from the suggestion row; only
`birthYear` remains, since it doesn't align with any xG Grid category and
so can't leak an answer the same way. If a future category is ever
birth-year-based, this caption would need the same treatment.
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
- Debounced at 150ms after the last keystroke, once the trimmed query
  reaches 2 characters — lowered from 275ms (2026-08-10) now that the
  request is properly cancelled on a superseded keystroke (see REQ-207's
  own test coverage), so a shorter debounce no longer risks piling up
  redundant in-flight requests; a failed suggestions fetch is swallowed
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

### SCREEN-02b: Suggestion entry point (REQ-215, S-089)

New for S-089 — no prior SCREEN entry covered this, and REQ-215's own
"guest vs. non-guest visibility"/"no retroactive rescoring" criteria left
the actual UI placement to `ui-implementer`'s judgment. Documented here
after the fact per this doc's own discipline for undocumented gaps found
mid-build.

**Placement decision.** Two trigger conditions exist (REQ-215): a submitted
guess scored incorrect, or a REQ-211 live lookup for that guess timing out.
Both are handled the same way, inside `GuessInput` (SCREEN-02) itself,
rather than adding anything to the grid cell (`CellState`, SCREEN-01a):

- The grid cell has a deliberately fixed, small footprint (REQ-214's
  "fixed-cell-footprint guarantee") that was never designed with room for
  an extra interactive element, and the incorrect states (SCREEN-01a states
  2/3) already show no interactive controls at all — adding one there would
  be a second, uncoordinated change to a constraint this document treats as
  load-bearing.
- The live-lookup-timeout trigger already has a natural home: `GuessInput`
  already stays open and shows the timeout's error inline on that path
  (unchanged by this story) — the sheet, not the cell, is already where
  this player is looking at the moment either trigger fires.
- Consequently, `GuessInput`'s prior "closes immediately on any scored
  result, correct or incorrect" behavior changes: a **correct** result
  still closes the sheet immediately, exactly as before. An **incorrect**
  result now keeps the sheet open and replaces the plain form with a brief
  outcome view, the same "replaces the form, header/Cancel stay put" shape
  SCREEN-02a's disambiguation prompt already established.

```
Incorrect result (direct submission or a disambiguation resubmission):
┌─────────────────────────────┐
│ 🇫🇷 France × [AFC] Arsenal   │
│                               │
│ ✕ Not a match.                │
│ You can try again, or         │
│ suggest a correction below.   │
│                               │
│ [ Suggest a correction ]      │  ← collapsed entry point (see below)
│                               │
│    [ Try another guess ]      │
│              [ Close ]        │
└─────────────────────────────┘

Live-lookup timeout (REQ-211) — the existing inline error, now with the
entry point added alongside it; the form itself is untouched/resubmittable,
exactly as before this story:
┌─────────────────────────────┐
│ 🇫🇷 France × [AFC] Arsenal   │
│ ┌───────────────────────────┐│
│ │ ronaldinho                ││
│ └───────────────────────────┘│
│ We couldn't verify this       │
│ guess against our live data   │
│ source in time. Please try    │
│ again.                        │
│                               │
│ [ Suggest a correction ]      │
│                               │
│         [ Submit guess ]     │
└─────────────────────────────┘
```

- **Entry point states**, all via one `SuggestionEntry` component mounted
  at either trigger site above:
  - **Non-guest, collapsed:** a single `surface-sunken` button, "Suggest a
    correction" — clicking it expands an inline form (player name
    read-only/pre-filled from the triggering guess, a club(s) field, a
    nationality field, Cancel/Submit).
  - **Guest:** the same button, rendered `disabled`, paired with text
    copy ("Register for a full account (Settings → Save your progress) to
    suggest a correction here.") — **present but inert, never hidden**
    (REQ-215's "advertised, not hidden" rule), same "disabled control +
    explanatory text" pattern REQ-717 already uses elsewhere for a guest.
    The guest restriction is enforced server-side regardless of what this
    button shows (REQ-215) — this is advertising, not the actual gate.
  - **Submitted:** the form is replaced by a short confirmation line
    ("Thanks — an admin will review this. It won't change this guess's own
    score.") — the explicit "won't change this guess's own score" clause is
    deliberate, not incidental copy: REQ-215's 2026-08-01 "no retroactive
    rescoring" decision means nothing on this screen may imply otherwise.
- **Tokens only**, no new ones: `surface-sunken`/`surface-card` for the
  collapsed button/expanded form (mirrors `GuessInput`'s own suggestions
  list treatment), `accent-red` for the ✕ icon (text/icon use, already
  measured to pass contrast as-is per §2), `text-muted` for hint/guest/
  confirmation copy, `accent-green-text` for the Submit button — same
  palette `GuessInput.css` already uses throughout.
- **Never color-only**: "Not a match." is real text next to the ✕ icon
  (§6), same as every other correct/incorrect signal in this document.
- **"Try another guess"** returns to the plain form for a genuine second
  attempt without closing/reopening the sheet — only offered when the cell
  isn't locked yet; once both attempts are used, only "Close" remains and
  the hint text says the cell is locked instead.

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
│ Global leaderboard                    (ⓘ) │
│ Lowest total wins                          │
├───────────────────────────────────────────┤
│ [All-time] [Current Round] [Previous       │
│  Rounds] [Time Windows]                    │
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
reused, not a second one invented.

**Player names are navigation targets to SCREEN-13 (REQ-411, S-179, added
2026-08-24):** every row's display name in the main ranked list (across all
four scopes below) is a real link — `accent-green-text`, underlined, same
44px row height already covers the touch-target floor — opening SCREEN-13's
stats/profile view for that player. This includes the requesting user's own
row when it's already visible in the loaded list (a deliberate judgement
call — see `LeaderboardRowsList.tsx`'s own comment for why a partial list
where only one row's name stays inert plain text would read as broken
rather than intentional). The separate pinned "you" footer row (REQ-607,
described under **Pagination** below) stays plain text — it already
unambiguously means "you," and Settings has its own dedicated "My stats"
entry point to the same destination (SCREEN-08).

**Game switcher (built, ADR-0043/`requirements-document.md` REQ-410,
`docs/backlog.md` S-087, 2026-08-02 — see that entry's "Built as" for the
full implementation, including the backend `gameKey` query-param work it
turned out to require):** once a second game exists, the **All-time**
scope above can no longer mean one thing —
`GetGlobalLeaderboardAsync` is scoped per `GameKey` (ADR-0043), so "the"
all-time ranking becomes "xG Grid's all-time ranking" and "xG Path's
all-time ranking," never one blended number. A game switcher — the same
plain underline-tab pattern used everywhere else on this screen, not a
new control type — sits above the `[All-time] [Current Round]...` scope
row, one tab per game (same name/order as SCREEN-09's tiles and
`HeaderNav`'s "Games" list: xG Grid, then xG Path). Switching games
re-fetches whichever scope tab is currently selected, scoped to the newly
selected game — it does not reset the selected scope tab back to
All-time. This affects **every** scope in this section (Current Round,
Previous Rounds, and Time Windows already take an explicit `gameKey`
today per their own REQs — REQ-407/408/405 — only **All-time** is the
scope this switcher newly makes possible), so the switcher sits above all
four scope tabs, not duplicated per scope.

**Scoring explainer entry point (REQ-213, S-068, added 2026-07-21):** the
`(ⓘ)` shown in the header above, next to the "Global leaderboard" title —
quiet, no-accent treatment (`text-muted`), same visual weight as
SCREEN-01's own `(ⓘ)` next to the round timer, not a second bolder style
for the same kind of control. Opens the exact same SCREEN-06 explainer
component SCREEN-01 already opens, reused rather than a second,
leaderboard-specific explainer (see SCREEN-06's own 2026-07-21 note for why
one component, not two). Reachable regardless of which scope tab below is
selected or whether that scope's data is loading, empty, or errored — it
reads no scope/round state — and opening or closing it never discards a
selected scope tab or a loaded "Load more" page.

Four scopes:
- **All-time** (REQ-401/404/409): ranks players by the **median** of their
  per-round scores, not a running sum, and only once a player has played at
  least 5 qualifying (closed, ≥1-guess) rounds — a player below that
  threshold simply doesn't appear on the list yet, rather than appearing
  with a misleadingly small sample. A league member who has never submitted
  a single guess is excluded entirely, never ranked first with a default
  total of `0` (REQ-404). The lowest-wins golf framing above applies to the
  median exactly the same way it applied to the previous running-sum
  ranking — unchanged by this switch (REQ-409, S-060). This is the default
  scope shown on first load. **Status note (2026-07-21):** this bullet
  previously described a plain running-sum total unchanged since v0.1 — the
  median/participation-gate ranking was actually decided and built
  2026-07-20 (REQ-409, S-060) and the never-played exclusion 2026-07-20
  (REQ-404, S-056), but this section was not updated at the time; corrected
  here as part of S-068, the same story that gave a player somewhere to
  actually read this explanation (SCREEN-06's entry point above).
- **Current Round** (REQ-407/ADR-0031): the active round's own
  leaderboard, recomputed live on every read. Rows and the running total
  render with the same "~N pts estimated" wording SCREEN-01's live cell
  value already uses (never presented as a locked final), with an
  explicit "Live — estimated, can still change until the round closes."
  note under the tabs. "No round is currently active — check back once
  one starts" is a plain informational empty state (not an error) when
  nothing is active; "No one has played this round yet" is the separate,
  distinct empty state for an active-but-unplayed round. **Status note
  (2026-07-21):** once a participant (≥1 guess anywhere in this round) has
  made their first guess, every other cell they haven't touched at all
  counts at the maximum score in this running total, the same value a cell
  locks at once the round closes without a correct guess — decided/built
  2026-07-20 (REQ-406/407, S-056) but not previously reflected here; a
  non-participant is excluded from this scope entirely, unaffected.
- **Previous Rounds** (REQ-408): a browsable list of closed rounds
  (labeled by their `closedAt` timestamp — there is no round-number field
  to fall back on), drilling into one round's own locked, final
  leaderboard (plain "N pts", never "estimated").
- **Time Windows** (REQ-405, S-027): a calendar-aligned (never rolling)
  leaderboard summed only
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

**Status note (2026-08-24, grouped sub-navigation per REQ-516/S-177):** the
page is no longer one long scrolling stack — it now opens with a persistent
tab bar (`role="tablist"`) with five groups, in this order: **Users**
(account metrics, guest force-clear, user deletion — the default/opening
group), **Grid** (unverified data review, player suggestions entry, round
control), **Path** (xG Path cycle control), **Announcements** (the
announcement banner), **Issues** (incident reports entry). Only one
group's sections are visible at a time; every group is always mounted and
toggled via the `hidden` attribute, never conditional rendering, so
switching groups never re-fetches a section that's already loaded — the
same "always mounted, active-controlled" pattern SCREEN-03's own
leaderboard scope tabs (`LeaderboardScreen.tsx`) already established. The
tab bar itself reuses that same plain underline-tab treatment (a flush
bottom border, an active tab's underline in `--color-accent-green`, no new
tokens) rather than inventing a new control, matching this screen's
existing "plainer/denser, working tool" character. Production-only
section-hiding is unaffected: round control and user deletion still fully
unmount (not merely hide behind an unselected tab) when
`ASPNETCORE_ENVIRONMENT == Production`, nested inside their respective
group exactly as before. No section below moved groups relative to its
REQ; only the page's outer layout changed. A slot was reserved
in the "Users" group for REQ-517's avatar-moderation section; S-183 built
it (2026-08-24), rendered directly below the account-metrics section
(before user deletion). Each pending row is a plain-list item (reusing
this screen's existing `admin-screen__row`/`admin-screen__list` treatment,
no new list styling) showing a 64px rounded image preview (`8px` radius,
matching every other rounded element already in this file; `object-fit:
cover` so a non-square upload doesn't distort), the submitter's display
name, and the submission time, with Approve/Reject buttons per row (the
existing `admin-screen__inline-form-actions` side-by-side treatment, not
the stacked confirm-step pattern `UserDeletionSection` uses, since these
are routine per-row actions rather than a rare destructive one). No new
token was introduced. The section's own heading carries the pending-count
badge ("Avatar moderation (N)", omitting "(N)" at zero) — the same inline
heading-badge convention `UnverifiedDataSection`'s "Unverified data (N)"
heading already established one group over, chosen over REQ-512's
button-label badge convention (`PlayerSuggestionsEntry`) because this
section has no separate click-through screen to badge the button of.

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

**Status note (2026-08-17, REQ-304/S-135):** the round label below was
previously the raw `roundId` GUID rendered as visible text
(`RoundControlSection.tsx`'s only such spot in the product). It now reads
`"Grid Round #{sequenceNumber}"`, using the `Round.SequenceNumber` field
REQ-304 added — no raw GUID is rendered anywhere in this section any more.

```
┌─────────────────────────────────────────────┐
│ Round control — xg-grid                       │
├─────────────────────────────────────────────┤
│ Grid Round #14 · ends 2026-07-20T18:00:00Z    │
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

**Accounts / guest-clear (REQ-507/508, added 2026-07-25) — always rendered,
no `ASPNETCORE_ENVIRONMENT` gate, unlike round control/user deletion above:**
these two REQs are explicitly visible in every environment including
Production (see each REQ's own "Scope note"), so — unlike round control and
user deletion — this pair is **not** nested inside the same
`activeRound !== null` visibility gate; it renders and fetches
unconditionally. **Status note (2026-08-24, REQ-516):** since the grouped
nav above moved this pair into the "Users" tab (alongside user deletion)
rather than the "Grid" tab that holds unverified data, "right after the
unverified-data section" no longer describes its on-page position — it now
sits directly above user deletion within "Users," unconditional fetch
behavior otherwise unchanged.

```
┌─────────────────────────────────────────────┐
│ Accounts                                       │
├─────────────────────────────────────────────┤
│ Total users        Current guests   Claimed   │
│      42                    7         guests    │
│                                          3      │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│ Guest accounts                                 │
├─────────────────────────────────────────────┤
│ Deletes every current guest account            │
│ immediately — a manual remedy you can use any  │
│ time, separate from the scheduled automatic    │
│ purge.                                          │
│                                                 │
│ [ Force clear guests ]                          │
│   (click) → fetches the dry-run count, then:   │
│   [Yes, delete all N guest accounts] [Cancel]  │
└─────────────────────────────────────────────┘
```

Judgment calls made without an existing spec to follow (`ui-implementer`,
S-073 — corrected from an earlier "S-076" reference here, which didn't
match any actual backlog story), recorded here per this repo's own
discipline rather than left as an unreviewed implementation-only detail:

- **A genuinely new component, not a reuse:** the metrics readout has no
  existing row/list class that fits a label+value pair, so it's a new
  `.admin-screen__metrics`/`.admin-screen__metric` pairing (tokens only —
  `--space-*` for gaps, `--color-text-muted`/`--color-text-primary` for
  label/value). The numeral value reuses the shared `.mono-figure` utility
  (`index.css`) for the mono-face/tabular-figures rule (§2's "any number
  meant to be compared at a glance"), and reuses `.admin-screen__title`'s
  existing 18px size rather than inventing a new one (§7 already flags this
  document has no formal type scale — this follows the existing
  reuse-what's-already-used convention rather than compounding that gap).
- **Two sections, not one:** "Accounts" (REQ-507, read-only) and "Guest
  accounts" (REQ-508, the destructive bulk action) are two separate
  `admin-screen__section` cards, matching this screen's existing
  one-card-per-REQ convention (unverified data / round control / user
  deletion are likewise separate cards) rather than merging a read-only
  view and a destructive action into one box.
- **Confirm-step copy strengthened per REQ-508's explicit acceptance
  criterion:** clicking "Force clear guests" fetches the dry-run count
  first, then reveals "Yes, delete all N guest accounts" / "Cancel" — a
  stronger two-step confirm than "Yes, end round now"/"Yes, delete this
  user permanently" specifically because the count is embedded in the
  confirm button's own label, not shown only as separate nearby text.
- **Zero-count special case (a judgment call, not in REQ-508's acceptance
  criteria):** if the dry-run count is 0, the UI shows "No guest accounts to
  clear right now." (the same muted/empty-state styling as "No unverified
  data to review.") instead of a confirm prompt reading "Yes, delete all 0
  guest accounts," which would be an odd, actionable-looking control for an
  action that would do nothing.
- **Per-account outcome list reuses the exact pattern REQ-503's bulk
  approve/remove already established** (`admin-screen__list`,
  `admin-screen__approval-result`/`--failed`, a "Dismiss" button) — one line
  per account (`{userId} — {outcome text}`), with `Succeeded` → "Cleared.",
  `NotFound` → "Not cleared — this account no longer exists.", and `Failed`
  → the server's own `errorMessage` folded into the sentence ("Not cleared —
  {message}."), never a raw enum value or a bare "failed." No player-facing
  display name exists for a guest account to show instead of its raw
  `userId` (guest accounts have no email; `displayName` exists but wasn't
  worth a second server round-trip for this admin-only, low-frequency
  action) — flagged here rather than silently treated as sufficient forever.
- **A successful clear refreshes the "Accounts" metrics readout** (the same
  `refreshMetrics` the "Accounts" section's own load uses) so the guest
  count visibly drops without a manual page reload.
- **403 handling deliberately differs from round control/user deletion's
  own 404-as-hidden probe:** a 403 from `GET /admin/accounts/metrics` hides
  both cards (not just "Accounts") rather than flipping the whole page to
  access-denied — REQ-501/502/503's unverified-data fetch already owns that
  page-level decision, and a non-admin token would already have 403'd there
  first in practice; this is defensive, not the primary access-control path.
  A 401 still escalates via the same `onAuthError` callback every other
  admin action in this file uses.

**Player refresh from Wikidata — standalone entry point removed
(2026-08-24, REQ-514 deprecated in favor of REQ-515):** REQ-514 originally
added a standalone "Refresh a player from Wikidata" section here — a
plain-text `Player` id (GUID) input plus a submit button, rendered
unconditionally in every environment. It is now **removed**: nothing else
in this admin UI ever surfaced a raw `Player` id for that field to consume
in the first place (product-owner decision), and REQ-515 (see this
section's own note, below) reaches the identical action inline from admin
player search instead, with no id-entry step at all. `SCREEN-04` no longer
has a section here between unverified-data review and round control.

The four-field changed/unchanged rendering and the 404/409/503 error
wording REQ-514 originally specified are NOT gone — they now live in the
shared `PlayerRefreshFieldsList` component (`describePlayerRefreshField`/
`describePlayerRefreshError`), which REQ-515's inline entry point uses
directly rather than reimplementing:

- Each field renders as its own line reading `"{Label}: Changed — "{old}"
  → "{new}""` or `"{Label}: Unchanged — "{value}""` — the word "Changed"/
  "Unchanged" is real text on every row, never a color-only signal (§6). A
  missing/null value renders as `"(none)"` rather than a blank string. A
  response where nothing changed still renders all four rows as unchanged
  with their current stored values, never a blank or empty result.
- **Changed/unchanged color pairing is a narrow token reuse, not a new
  value:** `.player-refresh-fields__field--changed` reuses `accent-green-text`
  — the same token `.admin-screen__confirmation`/`.suggestions-screen__confirmation`
  already use for a positive outcome ("Deleted.") — and `--unchanged` reuses
  `text-muted`, the same token used for secondary/no-action-needed text
  throughout this admin UI. Both colors are decorative reinforcement only;
  the "Changed"/"Unchanged" text label carries the actual meaning either
  way.
- **404 and 409's messages are UI-authored, not server-sourced; 503's
  reuses the server's own `detail` text:** REQ-513's `404` has no response
  body (`Results.NotFound()`), so "No player found with that id." is
  UI-authored in `PlayerRefreshFieldsList.tsx`'s `describePlayerRefreshError`,
  mirroring `UserDeletionSection`'s own "No user found with that email."
  precedent for the same reason. `409` does have a server `detail`, but its
  wording (mentioning `WikidataQid` and a cross-reference to REQ-510) is
  too internal for an admin-facing message, so "This player has no
  Wikidata id to refresh from." is UI-authored instead. `503`'s server
  `detail` — "We couldn't reach Wikidata to refresh this player. Please
  try again." — already matches the required wording as-is, so that one is
  read via the shared `describeError` convention every other admin action
  in this file already uses for its non-specifically-handled error path.
- No confirm/cancel step for the refresh action itself, wherever it's
  triggered from: this action is non-destructive, it can only apply
  already-trusted Wikidata data (ADR-0032), so it never needed SCREEN-04's
  two-step-confirm pattern for irreversible actions. While a request is in
  flight the triggering control is disabled and reads "Refreshing…", the
  same disabled-while-submitting pattern "Delete user" already uses.

**WikidataQid display + inline refresh in player search results (REQ-515,
added 2026-08-24):** `PlayerReviewPanel` — the shared "found a matching
Wikidata player" result component behind both `SuggestionsScreen.tsx`'s
pending-suggestion review flow (REQ-509) and its standalone manual-search
flow (REQ-510) — now always shows the found player's `WikidataQid` as
plain text alongside the existing name/nationality/clubs fields, and, only
when a local `Player` row already exists for that QID, an inline "Refresh
from Wikidata" button. Activating it calls REQ-513's endpoint directly and
renders the same `PlayerRefreshFieldsList` result (changed/unchanged, per
field, old→new) described just above — same component, same tokens, same
error wording, reused rather than reimplemented. No confirm step, same
non-destructive reasoning as REQ-513/514. `SuggestionsScreen.tsx`/
`PlayerReviewPanel` still has no formal `SCREEN-xx` entry of its own
(pre-existing gap, not introduced by this change) — this note lives here,
next to REQ-513/514's, since it's a direct extension of that same feature;
a future pass giving `SuggestionsScreen` its own `SCREEN-xx` entry should
fold this note in there instead.

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

**Status note (2026-07-25, sign-in latency investigation, ADR-0037's
third amendment):** this screen's password re-confirmation step gained a
real, visible Cloudflare Turnstile checkbox
(`.delete-account-screen__turnstile`, same reversal from invisible mode
described in §7's SCREEN-00 status note) sitting inline in the form,
empty until submit. No new tokens.

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
│                                 │
│ The all-time leaderboard ranks │
│ players by median score, not a │
│ total — lower is still better. │
│                                 │
│ You need 5 qualifying rounds   │
│ before you appear on the       │
│ all-time list.                 │
│                                 │
│ A player who's never guessed   │
│ isn't ranked. In Current       │
│ Round, an untouched cell       │
│ counts at the max once you've  │
│ made your first guess.         │
└───────────────────────────────┘
```

**S-041, REQ-213.** Opened from either of two equivalent `(ⓘ)` entry
points — SCREEN-01's header, next to the round timer, and (added 2026-07-21,
S-068) SCREEN-03's header, next to the "Global leaderboard" title — both
opening this exact same component with identical content, never a second,
divergent explainer keyed to whichever screen opened it (see the
2026-07-21 status note below). A modal (`role="dialog"`,
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

**Second entry point + content expanded again (2026-07-21, `docs/backlog.md`
S-068, REQ-213).** Raised because this explainer's content predated two
later ranking changes that became genuinely player-visible on SCREEN-03 but
were explained nowhere a player actually reads: REQ-409's median/≥5-round
participation gate (decided/built 2026-07-20) and S-056's fairness fix
(never-played members excluded from ranking, REQ-404; an untouched cell in
the Current Round scope counting at max, REQ-406/407). Two changes, both
shown in the mock above:
- **Reachability:** SCREEN-03 gained its own `(ⓘ)` entry point in its
  header, next to the "Global leaderboard" title — quiet/no-accent styling
  matching SCREEN-01's own `(ⓘ)`, not a bolder or differently-styled
  control for the same purpose. It opens this exact same component, not a
  second, leaderboard-specific explainer: a second component would
  inevitably drift from this one over time, exactly the kind of
  divergent-copy problem the 2026-07-14 content expansion above already
  existed to avoid repeating per-cell. The component takes no new prop and
  reads no round/grid/scope state, so it renders identically regardless of
  which screen's entry point opened it — a player who opens it from the
  grid screen sees the same ranking content described below, and vice
  versa. Opening or closing it from SCREEN-03 never discards a selected
  scope tab or a loaded "Load more" page (same `explainerOpen`-independent-
  of-other-state pattern the grid screen's own entry point already uses).
- **Content:** three more paragraphs, alongside the six above — the
  all-time scope's median ranking and its unchanged "lower is better"
  framing (REQ-409); the ≥5-qualifying-round gate below which a player
  simply doesn't appear yet (REQ-409); and the never-played exclusion plus
  the Current Round untouched-cell-counts-at-max rule together (REQ-404/
  406/407, S-056). These are the same nine content points required by
  `requirements-document.md`'s matching REQ-213 acceptance criteria — this
  section states the copy/placement, that doc remains the source of truth
  for which concepts are required.

**Bug fix (2026-07-21, same-day follow-up to S-068):** the content growth
above (six to nine paragraphs) pushed the card past the viewport height on
short/mobile screens — neither `.scoring-explainer` nor its backdrop had a
`max-height`/`overflow-y`, so the excess content overflowed off-screen with
no way to reach it, reported by a player as breaking the UI. Fixed by
giving `.scoring-explainer` a bounded `max-height` (accounting for the
backdrop's own padding) and `overflow-y: auto`, so the whole card — header
and close button included — scrolls as one block rather than a sticky
header; simple, not over-engineered, since nothing here demands a pinned
header. `GuessInput`'s card (SCREEN-02/02a) had the identical gap and was
fixed the same way for consistency; see `requirements-document.md`'s
matching REQ-213 note for the exact values.

### SCREEN-07: Header navigation (mobile menu)

```
┌──────────────────────────────┐
│ xG Arcade            [☰ Menu]│
└──────────────────────────────┘
        ↓ (toggle activated)
┌──────────────────────────────┐
│ xG Arcade            [☰ Menu]│
├──────────────────────────────┤
│ Games ▾                       │
│   xG Grid                     │
│ Leaderboard                   │
│ Leagues                        │
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

Nav entries in the revealed list: "Games" (see below), "Leaderboard,"
"Leagues" (REQ-402/403 — this list itself was out of date until this pass:
it had been added to the real nav without a matching update here, since
fixed alongside the "Games" entry below), "Settings" (SCREEN-08, REQ-713),
and "Log out" — see SCREEN-08 for what replaced the previous standalone
"Delete account" and "Admin" links.

**Added 2026-07-25, REQ-720 (reverses S-029's earlier removal of a
"Games"/"Grid" nav pair — see that requirement's own "deliberate reversal,
not a silent contradiction" note): the "Games" entry.** A second,
independently-expandable disclosure *nested inside* the list above —
same accessible-disclosure pattern as the outer toggle (a real `<button>`
exposing its own `aria-expanded`), but activating it never navigates
anywhere; it only shows/hides a per-game list, Tier 0's being exactly one
entry ("xG Grid"). Selecting "xG Grid" navigates to the grid screen (the
same destination `GameSelectScreen`'s own "xG Grid" tile already triggers)
and closes both this nested list and the outer mobile menu, matching how
every other nav entry already closes the menu on selection. While the grid
screen is showing, "xG Grid" inside this list carries `aria-current="page"`
— the same convention "Leaderboard," "Leagues," and "Settings" already use.

This is deliberately a *different* kind of disclosure than the outer
toggle: the outer toggle only exists below the mobile breakpoint (the flat
row at/above it needs no collapsing), while "Games" is collapsed by
default *at every viewport* — it's a permanent accordion-style entry
within the nav, not a responsive affordance. At/above the breakpoint it
sits inline in the flat row and reveals a small anchored flyout beneath
itself on activation (never adding height to the row, so it can never be
what causes the row to wrap); below the breakpoint, nested inside the
already-vertical mobile dropdown, it reveals as an indented block in the
same vertical flow instead of a floating flyout (avoiding an overlapping
flyout-within-a-dropdown). Both treatments use only existing surface/
border/spacing tokens — no new color or motion (same "no new motion" rule
as the outer toggle above). Closing the outer mobile menu also collapses
"Games" back to closed, so it never reopens already-expanded the next
time the menu itself is reopened.

The "xG Arcade" header title (outside this nav entirely) is unaffected and
keeps navigating to `GameSelectScreen` exactly as before — REQ-720 keeps
both affordances deliberately: "Games" is a quick-jump shortcut reachable
from anywhere (including from inside another screen, e.g. the
leaderboard), while the title remains the route to the full landing/picker
screen shown right after login.

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

**Added 2026-07-21, REQ-717/ADR-0036:** a "Save your progress" claim/upgrade
section, rendered first (above the admin-only link), only while the
current account is a guest — the first thing a guest sees on this screen,
since it's the primary reason a guest would open Settings at all. Once
claimed, the section disappears immediately (no page reload) since the
caller's own state updates from the claim response the same way the
display-name save above updates from its own response. Hosts a three-field
form — Email, Password, Confirm password — with the same REQ-701
8-character/match password-policy checks and inline-error convention
`AuthScreen.tsx`'s signup form already established, and the same "server's
own detail text shown inline" convention used everywhere else on this
screen (a 400 — not currently a guest, or the email is already in use —
surfaces the server's own message verbatim). Button copy:
"Save my progress" (submitting: "Saving…"), matching the hint text's own
wording rather than a generic "Submit"/"Create account". No new tokens —
same `settings-screen__section` bordered-row treatment, same field/error/
submit-button pattern as the display-name section above it. **Not yet
given a wireframe in this document** — built functionally with the
existing token system only, same "flagged, not silently left out of sync"
treatment as this document's other unreviewed-screen gaps (see §7).

**Added 2026-08-24, REQ-411/S-179:** a "My stats" link, in its own bordered
row — same plain-link treatment as the admin-only "Admin" link above it,
but unconditional (every account, guest or claimed, admin or not, can view
its own stats). Opens SCREEN-13's stats/profile view scoped to the current
account's own id. Placed above the admin-only link, since it applies to
every account and the admin link doesn't.

### SCREEN-09: Game select (post-login landing)

```
┌───────────────────────────────┐
│ Choose a game                   │
├───────────────────────────────┤
│  ┌─────────────┐ ┌───────────┐ │
│  │   xG Grid    │ │  xG Path  │ │
│  │  Guess the   │ │ Guess the │ │
│  │  player from │ │ player    │ │
│  │  two clues   │ │ from a    │ │
│  │              │ │ revealed  │ │
│  │              │ │ career    │ │
│  └─────────────┘ └───────────┘ │
└───────────────────────────────┘
```

**Built as specified (S-085, 2026-08-01, `58a3ca2`/`3829e0d`) — resolves
the open question flagged in §7 (tracks `docs/decisions/0040-0043` and
`requirements-document.md` REQ-1201-1206).** `GameSelectScreen.tsx`
(REQ-303, S-021) shipped as a single hardcoded tile deliberately kept
unspecified here, since Tier 0 only ever had one game to choose from —
that reasoning stopped applying the moment a second game (xG Path)
actually existed. This entry is the spec for the multi-tile version,
matched exactly by the shipped code — no deviations:

- Tiles are laid out in a row that wraps to stacked on narrow viewports
  (same breakpoint SCREEN-07's header-nav toggle already uses,
  `max-width: 480px`) — no new responsive mechanism.
- Each tile: game name (`--font-display`) plus a one-line, plain-language
  description of the core loop (not marketing copy, not a tagline) —
  "Guess the player from two clues" / "Guess the player from a revealed
  career", matching how REQ-720's "Games" nav entry already names them.
  No imagery on the tile itself (no crest/logo asset exists for either
  game, and none is planned — see the crest/trademark note under
  SCREEN-10 below).
- Tokens only: `surface-card` tile background, `border-hairline` border,
  no new color, no per-game accent color — a tile's identity comes from
  its name and description text, not a color code, consistent with
  "the UI is deliberately quiet" (§2).
- Order matches `HeaderNav`'s existing "Games" list order (REQ-720): xG
  Grid first (the original game), xG Path second — never alphabetical,
  never reordered by recency, so the two lists (this screen, the nav
  menu) never disagree about game order.
- No loading state: both `GameSelectScreen.tsx`'s existing constant
  (`XG_GRID_GAME_KEY`) and its future second constant are client-side
  values, not a fetched list (S-021's own reasoning: "a 'list games' API
  would be building a catalog for a catalog of one" — still true for a
  catalog of two) — the tile row renders immediately, nothing to wait on.
- Selecting a tile navigates the same way `onSelectGame(gameKey)` already
  works today — no change to that mechanism, only to how many tiles call
  it.

### SCREEN-10: xG Path puzzle (clue reveal)

```
┌───────────────────────────────┐
│ xG Path          Puzzle 2 of 4 │
├───────────────────────────────┤
│ ┆                               │
│ ●─[AJ] Ajax · 74 apps          │
│ ┆                               │
│ ●─[JV] Juventus · 94 apps       │
│ ┆                               │
│ ●─[IM] Inter Milan · 88 apps    │
│ ┆                               │
│ ●─Ajax 2001–04 · Juventus       │
│    2004–06 · Inter Milan        │
│    2006–09                      │
├───────────────────────────────┤
│ [ Guess the player…      ] [Guess]│
│         Clue 4 of 7            │
└───────────────────────────────┘
```

**Built as specified, 2026-08-01 (S-086, `18b1cc2`/`928bd85`)** — one
deviation from the literal spec text, called out in its own status note
below (the photo-fallback treatment). Originally written design-only
against `requirements-document.md` REQ-1201-1206 and
`docs/decisions/0040-0042`; direction was validated against two working
prototypes (a growing-timeline concept and a spotlight-stepper concept)
before the growing timeline was chosen. Built entirely from the existing
token system — no new color, typeface, or animation family introduced:

- **Layout:** clues stack as nodes on a vertical connecting line — the
  literal career path being drawn as it's revealed, one node per clue,
  oldest at top. Every past clue stays visible (no collapsing, no
  scrolling-away) — reviewing everything learned so far is part of the
  puzzle, not a secondary concern. The guess input is pinned below the
  timeline, not inside its scroll area, so it's always reachable
  regardless of how many clues have been revealed.
- **Clue content and order**, exactly per REQ-1203 — this screen adds no
  new sequencing decision, only how it's rendered:
  1. Every one of the target's documented club stints, chronological,
     split across exactly 3 nodes ("turns") — the wireframe above shows
     the 3-clubs case (one club per turn, since `N=3` splits 1-1-1); for a
     longer career a single node bundles more than one club together
     (e.g. `N=10` splits 3-3-4, so the last node alone shows 4 clubs), each
     still showing its own name plus appearance count when known (never a
     placeholder like "0 apps" when unknown — the count is simply omitted
     for that club within the node)
  2. Once all 3 club-reveal nodes are shown, one further node bundles
     every revealed club's own start–end year range together (never one
     aggregate span across the whole career)
  3. Then, if still unsolved: position, nationality, age — one node
     each, in that fixed order
  4. National team caps are never a clue (REQ-1203's explicit exclusion)
- **Club identity:** the same placeholder initial-chip badge already used
  on SCREEN-01 (a colored circle with the club's initials) — no real
  crest artwork. This isn't a stopgap unique to this screen: real crests
  remain trademarked/licensing-unresolved (§2's existing "club crests
  deferred" note, ADR-0008), and switching the *source* (Wikidata
  instead of API-Football) doesn't change that — Wikidata's own
  club-logo files are typically tagged for Wikipedia-only fair use, not
  general reuse, so this is the same open question, not a new one.
- **Attempt/clue counter:** "Clue N of M" in `--font-mono`/tabular
  figures (same treatment as every other score/count in this app),
  directly under the guess input — M is that puzzle's own total clue
  count (REQ-1205's per-puzzle cap), never a fixed number across puzzles.
- **Motion:** each new node fades and rises into place (~400ms,
  `ease-out`-family curve) — deliberately the same *settle* character as
  the grid's existing badge-dock reveal (§3's "Signature element: badge
  dock" above), not a new motion signature for the platform. Respects
  `prefers-reduced-motion`: nodes simply appear, no animation, same
  fallback pattern already established for the badge dock.
- **Rejected guess:** reuses SCREEN-02's existing shake cue verbatim —
  this screen does not invent a third "try again" motion for what is
  the same underlying moment (a guess didn't match).
- **Solved state:** a trailing gold node (`accent-gold`/`accent-gold-text`
  per §2's "gold means settled/correct" rule) is appended AFTER every real
  clue turn (**bug fix, 2026-08-03 — see status note below for why "the
  final node turns gold," this bullet's original wording, is stale**) and
  shows the target player's name plus, when `Player.PhotoUrl` is set, their
  photo (REQ-214's existing infrastructure, reused as-is — not a new
  photo feature for this game) — falling back to the same initials-avatar
  treatment REQ-214 already established for a player with no photo on
  file (**stale — see S-086 status note below**), never a broken-image
  icon. Once solved, the guess input and "Guess" button disable; a "Next
  puzzle" action appears to advance through the round's remaining puzzles
  (REQ-1202) — advancing is always an explicit action, never automatic,
  consistent with how a correct grid cell also waits for the player to tap
  before revealing anything further. **Also as built (S-086):** "Next
  puzzle" appears once a puzzle is *locked* at all — solved, or locked
  unsolved after its 7-attempt cap (REQ-1205) is exhausted — not only in
  the solved case this bullet describes; without it, a player who used all
  7 attempts without guessing correctly would have no way to advance to
  the round's next puzzle. This is a deliberate scope addition beyond this
  bullet's literal text, not an oversight.
- **Puzzle position:** "Puzzle N of M" (plain text, `text-muted`) in the
  header, mirroring SCREEN-01's round-timer header row placement.

**Bug fix status note (2026-08-03, user-tester report): "the final node turns
gold," in the bullet above, is stale.** As originally built, the solved (and
locked-unsolved "Out of attempts") reveal REPLACED the last real clue turn's
own node instead of appending after it — for a single-club turn that's a
small cosmetic swap, but the same turn can carry several bundled clubs
(`PathClueSequenceBuilder`'s 3-3-4 split for a long career) or the bundled
year-range/position/nationality/age content, and replacing it wholesale
silently deleted that entire turn's real content the instant the puzzle
locked — directly contradicting this section's own "every past clue stays
visible" rule above, and exactly what a tester reported as "the latest shown
clue was removed upon correct answer." `PathTimeline.tsx`'s reveal (solved or
failed) is now its own trailing node, appended after every real clue turn
rather than displacing one — the bullet above is left as originally written
(not rewritten) so the now-corrected assumption stays visible, same
convention the S-086 status note below already follows.

**S-086 status note (2026-08-01): the "initials-avatar" fallback text
above is stale, not something the shipped code actually does, and never
matched REQ-214's own history.** SCREEN-01a's own no-photo mocks (earlier
in this document) show that REQ-214's no-photo case has, at every point in
its history, rendered a plain checkmark/points value at rest and the
player's name (plain text) plus checkmark once revealed — never an avatar
of any kind, initials-based or otherwise. There is nothing in REQ-214's
actual implementation, past or present, for this bullet's "initials-avatar
treatment" to be reusing. `PathTimeline.tsx`'s `SolvedNode` (S-086)
instead renders the player's name as plain text with no avatar element at
all (and no separate checkmark — this screen's gold node styling already
carries the "solved" signal SCREEN-01a's checkmark exists to give) — the
closest honest match to what REQ-214 has actually ever done, rather than
inventing a new avatar component this story was never asked to design.
The bullet above is left as originally written (not rewritten) so the
now-corrected assumption stays visible rather than silently smoothed over;
treat this status note, not the bullet's "initials-avatar" clause, as the
accurate description of what SCREEN-10 actually does.

**S-086 quality-gate follow-up status note (judgment call, flagged rather
than silently resolved):** `PathScreen.tsx`'s guess flow makes two network
calls per submission — `POST .../guesses`, then a follow-up `GET
/path/current` to pick up the newly revealed clue (see that component's own
doc comment for why a re-fetch, not a local patch, is the mechanism xG Path
uses). Neither this section nor REQ-1203/1204/1205 originally specified what
happens if the *second* call fails after the first one already succeeded.
Resolved as follows, not sketched anywhere before this note:
- **Re-fetch throws (network blip, transient 5xx, mid-session 401):** the
  player is never told the guess itself failed (it didn't — REQ-1205's
  attempt was already consumed server-side, and telling them otherwise would
  invite a retry that burns a second attempt for nothing). Instead a
  distinct, honest inline message renders below the guess input: "Guess
  submitted, but couldn't refresh — try reloading this screen." Plain text,
  `accent-red`, no icon — same "text-paired, never color-only" rule §6
  already applies everywhere else on this screen (the locked/solved copy
  above it).
- **Re-fetch resolves `null` (the round closed in the gap between the two
  calls):** treated identically to any other "no active round" case —
  transitions to this screen's existing empty state (`No puzzle to play
  right now`), rather than leaving the stale, pre-guess puzzle on screen
  indefinitely with no explanation.
Both are edge-case network/timing failures, not new deliberate product
states — no new token, color, or motion was introduced for either; the
warning message reuses `accent-red` exactly as `path-screen__status--error`
already does elsewhere on this same screen.

**2026-08-02 status note — live user-testing batch, three fixes, all
tokens-only (no new color/typeface/animation):**

- **Skip-to-next-clue.** The guess input used to hard-block an empty
  submission client-side ("Type a player name to submit a guess."), with no
  other way to move on without typing something a tester didn't want to
  guess yet. Since every guess submission — right or wrong — already
  advances the reveal by consuming one attempt
  (`PathClueSequenceBuilder.GetRevealedTurnCount` ties revealed-turn-count
  directly to `attemptCount`), an empty submission is now let through as an
  intentional skip rather than blocked, reusing the existing guess path
  rather than a new endpoint/flow. The submit button's label reflects the
  field's own content: **"Next clue"** while empty, **"Guess"** once
  text is entered — the tester's own suggested wording. Two judgment calls,
  recorded rather than silently decided (`PathGuessInput.tsx`):
  - **The rejected-guess shake cue does not fire for a skip.** A skip is a
    deliberate choice, not a wrong answer, so shaking the input would read
    as scolding the player for something they chose to do on purpose. The
    shake stays scoped to an actual incorrect guess.
  - **What's actually sent as `submittedName` for a skip:**
    `POST /rounds/{roundId}/cells/{cellId}/guesses` 400s on an empty/
    whitespace `SubmittedName` (`GuessEndpoints.cs`), so a literal empty
    string can't be sent without a backend change. Rather than touching
    that endpoint, the frontend sends a fixed placeholder, `"(skipped)"` —
    chosen (over an opaque value like a UUID) so a human ever looking at
    raw `Guess` rows can tell at a glance what happened. It can never
    collide with a real player name and is never shown to the player either
    way, since an incorrect guess never displays `SubmittedName` (S-029,
    `SCREEN-01a`).
- **Career-stint year-range layout.** The bundled year-range turn used to
  join every club's range into one inline paragraph with " · " separators
  (e.g. "Paris Saint-Germain 2017-19 · Lille 2019-23 · Juventus
  2023-present · Marseille 2025-present") — confirmed by a real screenshot
  from testing to read as a dense, hard-to-scan block once it wrapped on
  mobile. Each club/year-range pair now renders on its own line (a stacked
  block, `--space-1` gap — the same spacing token already used for
  `.path-timeline__content`'s own column, no new value) instead of being
  joined inline. Same content and club-to-range pairing as before
  (`revealedClubNames[index]`); only the layout changed.
- **Reveal-on-failure.** A puzzle that locked *without* ever being solved
  (`REQ-1205`'s fixed attempt cap exhausted) previously showed nothing
  beyond its last real clue — no reveal of the answer at all. `PathScreen`
  now passes its existing `locked` value (already computed for the "Next
  puzzle" button's own gating) down to `PathTimeline` alongside `solved`.
  When `locked && !solved` on the final node, a **distinct, non-gold**
  reveal renders: a red (`accent-red` — §2's existing "incorrect states"
  token, used directly for text/icon color the same way `CellState.css`'s
  own incorrect state already does, no darkened `-text` variant needed)
  **"✕ Out of attempts"** label, followed by the resolved player's name and
  photo (same photo-with-fallback-on-load-error structure as the existing
  `SolvedNode`, reused rather than duplicated). Deliberately **not** the
  gold "✓ Solved" treatment — reusing that would misleadingly imply the
  player got it right. This depends on a parallel backend change
  (`PathEndpoints.cs`) populating `resolvedPlayerName`/
  `resolvedPlayerPhotoUrl` whenever a puzzle is `locked` (solved OR
  attempt-cap-exhausted), not only when `isCorrect` — until that ships, the
  frontend renders the "Out of attempts" label with no name/photo line
  (never a broken "it was null" line), and picks the name/photo up with no
  further frontend change once the backend field is populated.

**2026-08-04 status note — round end-time indicator added, wireframe
above now stale on this one point.** A product owner asked whether SCREEN-10
had the same round-end-time affordance SCREEN-01 has (REQ-303's 2026-07-21
addition); it didn't. `PathScreen.tsx`'s header now shows the same
`"Ends in {D}d {H}h"`/`"Ends in {H}h {M}m"`/`"Ends in {M}m"`/`"Ending soon"`
indicator SCREEN-01's header shows (`.grid-screen__end-time`'s exact
counterpart, `.path-screen__end-time`) — same wording rules, same
computed-once-at-fetch-time behavior (no live tick), same
accessible-name/keyboard-focus treatment. See REQ-303's own acceptance
criteria for the full format/threshold rules (not restated here) and
REQ-1203's 2026-08-04 status note for the requirements-side record of this
addition. Placed next to the "xG Path" heading, inside a new
`.path-screen__title-row`, the same relative position SCREEN-01's own
end-time indicator occupies in its header row — the ASCII wireframe at the
top of this section still shows the pre-2026-08-04 header (`xG Path
Puzzle 2 of 4` only) and is not redrawn here, same "leave the stale mock,
add a correcting note" convention this document already uses for SCREEN-01's
own 2026-07-21 correction above. No new color, typeface, or animation —
reuses `--color-text-muted`/13px/`--touch-target-min`, the same values
`GridScreen.css`'s `.grid-screen__end-time` already uses.

**2026-08-08 status note (REQ-1206 gap closed — the puzzle's locked point
value is now shown, a genuinely new SCREEN-10 element, not one this
section previously spec'd or anticipated).** A code-review pass
(`requirements-document.md`'s REQ-1206 "Status note (2026-08-08 — gap
identified via code review...)") found the clue-efficiency score
(REQ-1206) was computed and locked at round close but never shown to the
player anywhere on this screen. Resolved by adding one line to the
existing trailing solved (gold)/failed (red, "Out of attempts") reveal
nodes described above — `PathTimeline.tsx`'s `SolvedNode`/
`FailedRevealNode` — directly under the resolved player's name, the same
place `CellState.tsx` shows a locked cell's points on SCREEN-01a. This is
a judgment call, not literal spec text (flagged per this repo's own
"flag a judgment call rather than treating it as a minor implementation
detail" convention), for two things this document hadn't previously
decided:
- **Where it lives:** on the timeline's reveal node (`PathTimeline.tsx`),
  not `PathScreen.tsx`'s own header/status area — it's rendered in the
  same place, and gated by the same `locked` condition, as the resolved
  player name/photo it sits beside, rather than a separate element
  elsewhere on the screen.
- **Wording and color:** plain `"N pts"` (`mono-figure`, tokens-only, no
  new typeface) — deliberately never `"~N pts estimated"` (xG Grid's
  `LivePoints` wording, SCREEN-01a): unlike a grid cell's live estimate,
  `ClueEfficiencyScoringStrategy`'s formula has no dependency that can
  still change once a puzzle locks, so REQ-1206's own acceptance criteria
  forbid "~"/"estimated"/"provisional" wording here (see that REQ's
  "Important asymmetry from REQ-204's `LivePoints`" note). Colored to
  match the reveal node's own outcome accent — `accent-gold-text` on the
  solved node, `accent-red` on the failed one — mirroring
  `CellState.css`'s existing `.cell-state--correct .cell-state__meta`/
  `.cell-state--incorrect .cell-state__meta` convention for a locked
  cell's own points text, not a new muted tone invented for this screen.
  Per ADR-0021's golf-scoring convention (lower is better, same as every
  other score in this app), the number itself carries no celebratory
  styling implying a high value is good — the accent color signals
  solved-vs-not, never "good score vs. bad score."
No new color, typeface, or animation family was introduced — this reuses
`accent-gold-text`/`accent-red`/`--font-mono`/`.mono-figure` exactly as
SCREEN-01a and this section's own existing reveal nodes already do.

**2026-08-08 status note (REQ-213 gap closed — a scoring explainer was not
anticipated anywhere in this section before now).** A player who tested xG
Path directly reported "no scoring information in the game" — clarified on
follow-up to mean this screen had no `(ⓘ)` "How scoring works" entry point
or explainer of any kind, not the per-puzzle point value the same-day
REQ-1206 status note above already added. This section previously said
nothing about a scoring explainer for SCREEN-10 at all; the wireframe at
the top of this section is not redrawn (same "leave the stale mock, add a
correcting note" convention already used twice above for this same
screen). Resolved as a second entry point/component pattern, not a literal
extension of REQ-213's existing content:
- **Entry point:** a new `(ⓘ)` button (`.path-screen__info-toggle`) inside
  `.path-screen__title-row`, immediately after the round end-time
  indicator — the exact same relative position `GridScreen.tsx`'s
  `.grid-screen__info-toggle` occupies next to its own end-time indicator,
  same size/quiet/no-accent-color treatment (`--touch-target-min`,
  `--color-text-muted`, no new token).
- **Component: a new sibling, not a reuse of `ScoringExplainer.tsx` and
  not a `gameKey`-branched version of it.** REQ-213's 2026-07-21
  leaderboard extension reused `ScoringExplainer.tsx` verbatim precisely
  because its content is identical regardless of which screen opened it
  (both consumers describe the same grid/uniqueness/median mechanics).
  That reasoning doesn't transfer here: xG Path has no uniqueness concept,
  no live/locked point distinction (its locked score is final
  immediately, unlike a grid cell's live-then-locked value), and a wholly
  different clue/attempt-cap model, so reusing that component's content
  verbatim would state things about xG Path that are actively false, and
  branching every paragraph on a `gameKey` prop was judged to read worse
  and risk cross-game content bleed for a two-consumer case (this
  repo's own "duplication over premature abstraction for exactly two call
  sites" convention, CLAUDE.md). Built as `frontend/src/path/
  PathScoringExplainer.tsx` — its own content, but the same modal/
  accessibility shell as `ScoringExplainer.tsx` (`role="dialog"`,
  `aria-modal="true"`, Escape-to-close, focus moves to the close button on
  open and returns to the `(ⓘ)` trigger on close) duplicated rather than
  extracted into a shared hook/component, same two-call-sites reasoning.
- **Content**, verified against the actual backend implementation, not
  assumed: the fixed 7-clue sequence and its order (3 club-reveal turns,
  one bundled year-range turn, then position/nationality/age — one clue
  per wrong guess, halting immediately on a correct one); the fixed
  7-attempt cap, and that exhausting it locks the puzzle unsolved and
  reveals the answer; the golf-style scoring formula
  (`round(cluesUsed / 7 × MaxPointsPerCell)`), stated explicitly as
  "scored like golf, lower is better" rather than assuming the player
  already knows this convention from xG Grid; that an unsolved puzzle
  scores the same worst case as a correct guess that used every clue; and
  that a locked score is final immediately, with no live/provisional value
  to watch update — the deliberate opposite of SCREEN-01a's live-then-
  locked cell. No uniqueness or other-players'-answers language appears
  anywhere in this copy.
- **Tokens only** — `--color-surface-card`, `--color-border-hairline`,
  `--color-text-primary`/`--color-text-muted`, existing spacing scale, no
  new color/typeface/animation. `PathScoringExplainer.css` duplicates
  `ScoringExplainer.css`'s values rather than sharing the stylesheet, same
  two-call-sites reasoning as the component split above.
- **Known, pre-existing, out-of-scope gap flagged (not fixed here):**
  `LeaderboardScreen.tsx`'s `(ⓘ)` entry point still opens xG Grid's
  `ScoringExplainer` verbatim even when the leaderboard's xG Path tab is
  active, showing Grid-specific content that doesn't describe xG Path's
  rules. Pre-existing, unrelated to this screen, and out of this change's
  scope — noted here as a candidate for a follow-up, not addressed now.

### SCREEN-11: Footer incident-report entry point (REQ-903, ADR-0064)

New for this story — no prior SCREEN entry covered this. Built first
(2026-08-10) as a section inside SCREEN-08 (Settings); moved the same day,
directly requested, to the app-wide footer instead, documented here after
the fact per this doc's own discipline for undocumented gaps found
mid-build (same situation SCREEN-02b's own top note describes).

**Placement decision.** REQ-903's entry point needs to be usable from
whatever screen a player is actually looking at when something breaks —
Settings is the wrong home for that, since reaching it first means
navigating away from the very screen showing the problem. `App.tsx`'s
`<footer>` already renders unconditionally beneath `<main>` regardless of
`screen`, the same element that already shows the health-check status
(S-002) — the "Report a problem" button lives there, and opens
`IncidentReportDialog` as a modal over whatever screen is currently
showing, rather than navigating anywhere.

```
Footer (every authenticated screen):
┌───────────────────────────────────────────────┐
│                    API status: ok  Report a problem │
└───────────────────────────────────────────────┘

Dialog (opened over whatever screen was showing) — structured-fields
shape, added the same day as the footer relocation above:
┌─────────────────────────────┐
│ Report a problem          × │
│                               │
│ Title                         │
│ ┌───────────────────────────┐│
│ │ Short summary, e.g. "Grid  ││
│ │ freezes after guess submit"││
│ └───────────────────────────┘│
│                               │
│ Screen                        │
│ ┌───────────────────────────┐│
│ │ xG Grid               ▾   ││  ← defaults to whatever screen was
│ └───────────────────────────┘│    showing when this was opened
│                               │
│ What went wrong?              │
│ ┌───────────────────────────┐│
│ │ Steps to reproduce, if you ││
│ │ can — and what you        ││
│ │ expected vs. what          ││
│ │ actually happened…         ││
│ └───────────────────────────┘│
│                               │
│ Environment: https://…        │  ← read-only, not a field
│                               │
│         [ Send report ]      │
└─────────────────────────────┘
```

- **Component: `IncidentReportDialog.tsx`**, structural/accessibility shell
  taken from `GuestLogoutConfirm.tsx`/`ScoringExplainer.tsx` (`role="dialog"`,
  `aria-modal="true"`, backdrop-click-to-close, Escape-to-close, header
  `×` close button, focus moves in on open and returns to the footer button
  on close) — no new interaction pattern invented for this screen.
- **Guest visibility**: present but disabled — every field, including
  Title/Screen below — same "advertised, not hidden" rule REQ-215's
  `SuggestionEntry` (SCREEN-02b) already established, not a new decision.
  Signed-out (no session at all) renders no footer button at all, matching
  REQ-903's own 401 requirement — there is no meaningful "advertise it,
  disabled" state for a visitor who isn't authenticated at all.
- **Structured fields (2026-08-10, same day as the footer relocation,
  requested directly): Title/Screen are now mandatory, separate fields —
  a free-text box alone let reports drift into inconsistent shapes with no
  guaranteed way to tell what screen a problem happened on without reading
  every word.**
  - **Title**: a short text input, becomes the created GitHub issue's own
    title verbatim — so the issue list itself is scannable, not just each
    issue's body.
  - **Screen**: a `<select>` dropdown over a fixed, closed list mirroring
    `App.tsx`'s own `Screen` union (Choose a game / xG Grid / xG Path /
    Leaderboard / Leagues / Settings / Admin / Admin — Player suggestions),
    plus "Something else / not sure" for anything not tied to one screen.
    Pre-selected from wherever the dialog was actually opened (accurate by
    construction), but the player can change it — the problem might be
    *about* a different screen than whichever one they happened to be on
    when they noticed it.
  - **What went wrong?** (Description): unchanged position/shape, but its
    job narrows now that Title/Screen are split out — placeholder wording
    updated to prompt reproduction steps and expected-vs-actual, not a
    one-line summary (that's Title's job now).
  - **Environment**: shown as small, muted, read-only text under the
    form — never an editable field. Computed automatically from
    `window.location.origin` (the frontend's own deployed URL) the moment
    the dialog opens — directly answering "found in environment" without
    asking the player to know or type it, since the app already knows
    which URL it's being served from.
  - **Server-side formatting**: `IncidentReportService`
    (`XGArcade.Core.IncidentReporting`) turns these four fields into one
    fixed GitHub issue body template (`## Description` / `## Details` with
    Screen/Environment/internal-user-id/timestamp each under its own
    bolded label, in the same order every time) — the point of asking for
    structured fields in the first place: every issue this endpoint
    creates is shaped the same way regardless of what any individual
    player wrote, so triage never has to re-parse free text to find the
    screen or environment.
- **Screenshots: still explicitly out of scope.** Considered and
  deliberately deferred (unchanged since the footer-relocation pass) —
  GitHub's issue-creation API has no attach-a-file endpoint; the two real
  options are widening the PAT past ADR-0064's locked-in `Issues: write`
  scope (to write repo contents) or adding a new third-party image host
  (its own ToS review, secret, and privacy-policy disclosure, per
  CLAUDE.md's external-data-source rule) — both are decisions to make
  deliberately later, not something to fold into either of this story's
  two same-day passes.
- **Tokens only**: `--color-surface-card`/`--color-border-hairline` for
  the dialog card, `--color-text-muted` for the footer button, hint/
  placeholder-adjacent copy, and the read-only Environment line,
  `--color-accent-red`/`--color-accent-green-text` for error/success
  text — the exact same palette `SettingsScreen.css`'s claim/display-name
  forms already used before this moved, no new value introduced by either
  pass. The Title input and Screen `<select>` reuse the identical
  bordered-field treatment the Description textarea already had.

### SCREEN-12: Round-completion banner (REQ-1210, ADR-0083)

New for this story — no prior SCREEN entry covered this. Generic across
every game xG Arcade hosts (xG Grid, xG Path today, any game added
later) — one component, `RoundCompletionBanner.tsx`, rendered by both
`GridScreen.tsx` and `PathScreen.tsx` from the same shared trigger
(`lib/roundCompletion.ts`, ADR-0083), never a per-game copy.

```
Inline banner, in normal document flow above the grid/puzzle timeline —
NOT a modal, NOT a backdrop, cannot intercept a click meant for the
header nav or any other on-screen control:

┌─────────────────────────────────────────────────┐
│ Round complete                                   │
│ ~42 pts estimated              [View leaderboard] × │
└─────────────────────────────────────────────────┘
```

- **Placement and interaction model.** Sits inline, in normal flow, at the
  top of the screen's own content area — directly above `Grid`
  (xG Grid) or the puzzle timeline (xG Path). Deliberately not a modal:
  REQ-1210 asks for "immediate feedback," not an interruption that blocks
  the player from continuing to look at the board they just finished. A
  small "×" dismiss control hides it without discarding anything
  underneath (the grid/timeline is completely unaffected either way).
- **Points value wording — each game keeps its own existing convention,
  never a third one.** xG Grid shows "~N pts estimated" (REQ-204/213's
  existing provisional framing — another player's still-open guess on a
  shared cell can still change this total until the round actually
  closes, REQ-205). xG Path shows plain "N pts" (REQ-1206 — a locked xG
  Path puzzle's points are already final and never change). The banner
  component itself is agnostic to which wording it's showing — each
  screen formats its own points text and hands it down as a prop, so
  `RoundCompletionBanner.tsx` never has to know which game it's serving.
- **Leaderboard link.** "View leaderboard" takes the player straight to
  that specific round's leaderboard for that specific game — REQ-407's
  live view if the round hasn't closed yet, REQ-408's closed view,
  pre-drilled into that round (bypassing the closed-round list), if it
  has. See ADR-0083 for why this is in-memory navigation state through
  the existing screen-switch mechanism, not a URL route. The button
  briefly disables (never hides) while that live-vs-closed check
  resolves, so a fast double-click can't fire two navigations at once.
- **When it appears — REQ-1210 §7's open question, resolved
  conservatively.** Fires once, on the in-session transition from
  "not every cell/puzzle locked yet" to "every one now locked" — never on
  loading or navigating into an already-finished round (a page reload,
  or revisiting the screen later in the same or a later session, shows no
  banner). Whether it should ever replay on a later revisit is left
  genuinely open in `requirements-document.md`'s REQ-1210 §7 note — no
  per-player-per-round "have they seen this" state exists anywhere today,
  and this in-session-only default needs none.
- **Tokens only** — same card shell (`--color-surface-card`,
  `--color-border-hairline`) every other card in this app already uses,
  `--color-accent-green-text` for the heading and the primary button
  (the same treatment `GuessInput`'s submit button already established),
  `--color-text-primary`/`--color-text-muted` for body text, existing
  spacing scale (`--space-*`) and the shared `--touch-target-min` sizing —
  no new color or typeface. See §2 above for the settle-in animation
  itself and its `prefers-reduced-motion` fallback.

### SCREEN-13: Player stats / profile (REQ-411, S-179)

New for this story — no prior SCREEN entry covered this. One component,
`UserStatsScreen.tsx` (`frontend/src/users/`), used identically whether it's
showing the viewer's own stats or another player's — REQ-411 has no
own-only action, so there is no "edit" mode, no privacy toggle, nothing
this screen can do besides display figures for whichever `userId` it was
given.

```
┌───────────────────────────────┐
│ Back                           │
│ Alex's stats                   │
│ Lowest total wins               │
├───────────────────────────────┤
│ [xG Grid] [xG Path]            │
├───────────────────────────────┤
│ Rounds played            12    │
│ Best round            120 pts  │
│ Average round        142.3 pts │
│ All-time rank             #4   │
└───────────────────────────────┘
```

- **Reached from two entry points, never a top-level nav entry.** Settings'
  "My stats" link (own stats — see SCREEN-08's own entry, updated alongside
  this one) and, on the leaderboard (SCREEN-03), selecting any row's display
  name (another player's stats — the requesting user's own in-list row is
  included too, a deliberate judgement call recorded in
  `LeaderboardRowsList.tsx`'s own comment: leaving just that one row as
  inert plain text among an otherwise all-clickable list would read as
  broken, not intentional, and clicking your own name here is harmless).
  The pinned "you" footer row stays plain text — it already unambiguously
  means "you," and Settings already has a dedicated entry point to the same
  destination. No `HeaderNav` entry, deliberately — REQ-712/713 already
  consolidated standalone top-level links into Settings specifically to
  stop header overflow; this screen is reached the same gated way
  `AdminScreen`/`SuggestionsScreen` already are.
- **Heading always names whose stats are shown.** "{DisplayName}'s stats" —
  same heading whether this is the viewer's own account or someone else's,
  since the component itself has no own-vs-other concept beyond the
  `userId`/`displayName` props it was handed (App.tsx is the only place
  that knows which case it is, via which entry point set the in-memory
  navigation seed — see ADR-0083's "no router library" pattern, reused here
  exactly as `leaderboardInitial`/`LeaderboardRoundTarget` already
  established for SCREEN-03's own round-completion-banner deep link).
- **"Lowest total wins" note.** Same ADR-0021 correction SCREEN-03 already
  leads with, same token/placement (`text-muted`, directly under the
  heading) — the figures below are the same `FinalPoints`/median metric
  that note already governs, so the same golf-scoring reminder applies here
  too, shown unconditionally (not just in the populated state).
- **Game switcher.** Same plain underline-tab pattern as SCREEN-03's own
  game switcher (xG Grid, then xG Path — same order, same tokens,
  `accent-green` underline on the active tab), not a new control type.
  Switching games re-fetches this screen's stats scoped to the newly
  selected game.
- **Populated state.** Rounds played (REQ-409's existing qualifying-round
  definition — a closed round with at least one guess), best single round's
  `FinalPoints`, average `FinalPoints` (shown to one decimal place, trimmed
  when it's a whole number), and all-time rank — but rank is **omitted
  entirely**, not shown as zero or an error, when the player hasn't met
  REQ-409's 5-round ranking minimum, even though the other three figures
  are present. Same `mono-figure` tabular-numeral treatment (§2) every other
  numeric figure in this app already uses (leaderboard rank/points).
- **Zero-qualifying-rounds empty state.** `hasRoundsPlayed: false` (the
  API's own discriminator, `UserStatsResponse`) renders "No rounds played
  yet for this game." — a distinct, calm empty state (design-document.md
  §5: "empty states are invitations"), never a blank screen and never
  `roundsPlayed`/`bestFinalPoints`/`averageFinalPoints` rendered as `0`,
  which would misread as a real, played score of zero. Applies identically
  whether the zero-rounds account is the viewer's own or another player's —
  REQ-411's "Viewing another player's stats" acceptance criteria is
  explicit that this is the same presentation, not an error, in that case.
- **Not-found state.** A `userId` that doesn't exist (404) is a real, distinct
  error state — "This player couldn't be found." — never the same
  zero-rounds empty state a real player with no qualifying rounds gets. In
  practice this should be unreachable through either of this screen's own
  entry points (both only ever pass a `userId` sourced from a real account —
  the current session or a leaderboard row), but the API contract allows it
  and the UI branches on it explicitly rather than assuming it can't happen.
- **Tokens only** — same card/tab/status shell every other screen in this
  document already uses (`--color-surface-card`, `--color-border-hairline`,
  `--color-text-muted`, `--color-accent-green`/`--color-accent-green-text`,
  `--color-accent-red` for the error/not-found states, existing spacing
  scale, `--touch-target-min`) — no new color or typeface introduced for
  this screen.

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

- ~~Whether a dark theme is ever offered as a user preference, now that
  light is the default (reversed from v0.1's "dark only" assumption)~~ —
  **resolved 2026-07-20 (REQ-716):** yes — see §2's **Dark theme**
  subsection for the full token table and contrast derivations, and
  `requirements-document.md`'s REQ-716 for the mechanism decision (an
  explicit three-state System/Light/Dark toggle in Settings, persisted in
  `localStorage`, not an automatic-`prefers-color-scheme`-only approach).
  Token values are decided and contrast-verified; implementation is a
  separate, not-yet-built story.
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
  unreviewed de facto spec. **2026-07-21 (REQ-717/ADR-0036) addition to
  this same unreviewed screen:** a "Play as guest" button sits below the
  log-in/sign-up form, separated by a plain divider — a single tokens-only
  bordered button (`.auth-screen__guest`, same shape as
  `.settings-screen__admin-link`), plus a one-line hint ("No email needed.
  You can save your progress and pick a real account any time from
  Settings."). No new tokens. This addition should be captured by the same
  future SCREEN-00 entry, not left to compound the existing gap further.
- **2026-07-25 (sign-in latency investigation, ADR-0037's third
  amendment) addition to this same unreviewed screen:** the login/signup
  form and "Play as guest" button each gained a real, visible Cloudflare
  Turnstile checkbox (`.auth-screen__turnstile`, reversing the original
  invisible-mode captcha widget) — the form's checkbox sits between the
  error message and the submit button, "Play as guest"'s sits below that
  button, both empty until the corresponding action is actually submitted.
  Signup also gained a transient status line
  (`.auth-screen__turnstile-status`, `--color-text-muted`, matching the
  existing error text's 13px size) reading "Verifying again to log you
  in…" during the form's required second Turnstile render (a token is
  single-use, so signup and its immediate auto-login each need their own).
  No new tokens beyond `--space-1` for layout spacing. Same as the
  additions above: this should be captured by the future SCREEN-00 entry,
  not left to compound the existing gap further — now more consequential
  than the earlier, invisible-mode captcha additions, since this one has
  a real, visible footprint on the rendered screen.
- **SCREEN-08 (Settings) gained a guest claim/upgrade section (2026-07-21,
  REQ-717/ADR-0036), also not yet reflected in a revised wireframe below** —
  see that section's own status note for what was actually built.
- **A new, minimal header element (2026-07-21, REQ-717/ADR-0036) has no
  SCREEN-xx entry of its own either:** a thin banner
  (`.app__guest-banner`, `App.tsx`) reading "Playing as {display name}." with
  a "Save your progress" text-link action, rendered only while the current
  session is a guest, directly below the header and above the rest of the
  page — a deliberately low-effort nudge (REQ-717's own framing), not a
  redesign, using only existing tokens (`surface-sunken`,
  `border-hairline`, `accent-green-text`) and no new motion. Clicking it
  navigates to Settings (SCREEN-08), where the claim section above actually
  lives.
- ~~No SCREEN-xx spec exists for the post-login game-selection landing
  screen either~~ — **resolved 2026-07-26, see SCREEN-09.** Written ahead
  of the second game (xG Path) actually existing in code as a design-only
  spec (`requirements-document.md` REQ-1201-1206); **built as specified,
  2026-08-01 (S-085, `58a3ca2`/`3829e0d`)** — `GameSelectScreen.tsx` now
  renders both tiles (xG Grid, xG Path), matching this section exactly.
- **No SCREEN-xx spec exists for the unauthenticated splash/landing screen
  either** (`frontend/src/splash/SplashScreen.tsx`, added for REQ-719).
  Same gap and same reasoning as SCREEN-00/the game-selection screen above:
  kept deliberately minimal (an `<h1>` for "xG Arcade", a one-line tagline,
  and a single tokens-only primary button, no wireframe/copy/state review)
  rather than left unbuilt while a real spec was drafted. No new tokens —
  the title uses the existing `--font-display` family (sized larger than
  `app__title`'s 22px header treatment, since this is the screen's own
  hero) and `--color-text-primary`; the tagline uses `--color-text-muted`;
  the CTA button reuses `auth-screen__submit`'s exact token pairing
  (`--color-accent-green-text` fill, `--color-surface-card` label). No
  animation was added (REQ-719 doesn't call for one, and the badge-dock
  reveal remains the app's only deliberate bold motion moment). Needs a
  real SCREEN-xx entry (wireframe, copy review, any state beyond the single
  at-rest one it has today) rather than staying an unreviewed de facto
  spec, same as the other two gaps above.
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
- **A new site-wide element (2026-08-10, REQ-511) has no SCREEN-xx entry of
  its own either, same gap as the guest banner above:** the admin-managed
  announcement banner (`frontend/src/components/AnnouncementBanner.tsx`,
  `.announcement-banner`) — a full-width band reading the admin's current
  active message, mounted at the very top of `App.tsx`, above `<header>`
  and outside every auth-gated branch, so it renders identically for a
  logged-in user, a guest, and a fully logged-out visitor alike (REQ-511's
  own "no authentication of any kind" requirement). Deliberately reuses
  `.app__guest-banner`'s existing "quiet notice band" token pairing
  (`surface-sunken` background, `border-hairline` bottom border, centered
  text) rather than introducing a new color — the only new visual choice is
  bold (600-weight) text, to read as more prominent than that thinner
  session nudge, still using only the existing `text-primary` token. No new
  motion (renders/unmounts instantly on fetch resolution, no transition).
  The admin-only counterpart (`AdminScreen.tsx`'s `AnnouncementBannerSection`,
  an inline section — not a separate linked screen like SuggestionsScreen,
  since a single message field plus an activate/deactivate toggle doesn't
  warrant its own nav hop) reuses `AdminScreen.css`'s existing form/section
  tokens wholesale, adding only a `<textarea>` variant of the existing
  `.admin-screen__field input` rule (same border/background/padding, plus
  `resize: vertical` so a multi-line message field can't blow out the
  section's fixed width). Needs a real SCREEN-xx entry (wireframe, copy
  review, any state beyond the ones already listed here) rather than
  staying an unreviewed de facto spec, same recommendation as every other
  gap in this list.
