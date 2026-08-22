import { useEffect, useRef, useState } from 'react';
// REQ-213 (S-068, made game-aware 2026-08-08): the leaderboard's own `(ⓘ)`
// entry point opens this exact same explainer GridScreen.tsx already uses
// when the xG Grid tab is active — no new component, no new props, no
// leaderboard-specific content (see ScoringExplainer.tsx's own 2026-07-21
// doc comment and REQ-213's matching acceptance criteria). When the xG Path
// tab is active, `PathScoringExplainer` (below) opens instead — see the
// render call near the bottom of this file for the `gameKey` branch and its
// rationale.
import { ScoringExplainer } from '../grid/ScoringExplainer';
// REQ-213 (2026-08-08, second consumer): xG Path's own scoring rules share
// almost nothing with xG Grid's (no uniqueness, no live/locked distinction,
// a different attempt-cap/clue model — see that component's own doc
// comment), so it gets its own explainer component rather than a `gameKey`
// branch inside `ScoringExplainer.tsx`. Importing directly from
// `../path/PathScoringExplainer` mirrors the existing `../grid/
// ScoringExplainer` import immediately above — this file already imports
// across feature folders for exactly this "which explainer does the active
// game need" reason, so this is the same established pattern, not a new
// one.
import { PathScoringExplainer } from '../path/PathScoringExplainer';
// REQ-410/ADR-0043 (S-087): the same client-side `GameKey` constants
// GameSelectScreen/HeaderNav already use — no new/duplicate string literal
// per this repo's own established convention (see GameSelectScreen.tsx's
// own comment on why these stay plain constants rather than API-sourced).
import { XG_GRID_GAME_KEY, XG_PATH_GAME_KEY } from '../games/GameSelectScreen';
import { AllTimeLeaderboard } from './AllTimeLeaderboard';
import { LiveLeaderboard } from './LiveLeaderboard';
import { PastRoundsLeaderboard } from './PastRoundsLeaderboard';
import { WindowedLeaderboard } from './WindowedLeaderboard';
import './LeaderboardScreen.css';

export interface LeaderboardScreenProps {
  accessToken: string;
  onAuthError: () => void;
  // REQ-1210/ADR-0082: optional seed state for jumping straight into a
  // specific round+game's leaderboard, e.g. from the round-completion
  // banner's "View leaderboard" link (GridScreen.tsx/PathScreen.tsx). This
  // is in-memory initial state threaded through the existing screen-switch
  // mechanism (App.tsx), not a URL parameter — see ADR-0082 for why that
  // doesn't trigger ADR-0039's "add react-router" follow-up. All three are
  // read only once, via useState's lazy initializer, at this component's
  // own mount — a prop change on an already-mounted instance has no effect,
  // matching the "initial" naming; every existing call site (which omits
  // all three) behaves exactly as before. `initialRoundId` is only
  // meaningful alongside `initialScope: 'past'` (pre-drilled round detail,
  // handled by PastRoundsLeaderboard below) — passing it with any other
  // scope is simply ignored.
  initialGameKey?: GameKey;
  initialScope?: Scope;
  initialRoundId?: string;
}

// REQ-406/407/408/405 (S-053/S-054/S-027): a new, separate scope selector on
// the same SCREEN-03 screen — distinct from custom leagues' own "[Global]
// [My League ▾] [+ New]" tabs design-document.md's current SCREEN-03 mock
// describes (custom leagues exist as of REQ-402/403/S-063, but this screen
// still only reads the global league; this selector exists alongside a
// future league picker, not instead of it). 'all-time' is REQ-401/404's
// global leaderboard, ranked by REQ-409's median-per-qualifying-round score
// (>= 5 qualifying rounds) as of S-060 — no longer a raw locked-points sum.
// 'live' is REQ-407's standalone active-round scope. 'past' is REQ-408's
// browsable closed-round list + drill-in. 'window' is REQ-405's
// calendar-aligned (never rolling) round/week/month/year leaderboard.
//
// S-121: each scope's fetch/poll/cancel logic and rendering now live in
// their own component (`AllTimeLeaderboard`/`LiveLeaderboard`/
// `PastRoundsLeaderboard`/`WindowedLeaderboard`) — this file is now the
// thin orchestrator: header, game-key switcher, scope tab bar, the
// scoring explainer modal, and rendering whichever of the four matches
// `scope`. All four are always mounted (see each one's own `active` prop
// doc comment for why) — `scope` here only controls which one renders
// non-null output, mirroring exactly what the single pre-split component
// used to do by only calling one `render*()` function per render.
// Exported (REQ-1210/ADR-0082) so App.tsx can type the `initialScope` it
// passes down without redefining this union.
export type Scope = 'all-time' | 'live' | 'past' | 'window';

// REQ-410/ADR-0043 (S-087): once a second game exists, "the" leaderboard
// can no longer mean one thing — every scope's read is now scoped to one
// `GameKey`. Same name/order as SCREEN-09's tiles and `HeaderNav`'s "Games"
// list (xG Grid, then xG Path — never alphabetical), and xG Grid as the
// default (matching GameSelectScreen's own tile order/"still the only
// shipped game until S-085" precedent). Exported so each of the four scope
// components can type their own `gameKey` prop against the same type
// rather than redefining it.
export type GameKey = typeof XG_GRID_GAME_KEY | typeof XG_PATH_GAME_KEY;

// REQ-1210/ADR-0082: the shape GridScreen.tsx/PathScreen.tsx's round-
// completion banner hands to App.tsx to describe exactly one round's
// leaderboard — 'live' when the completed round hadn't closed as of the
// moment the link was activated (REQ-407, no roundId needed there: a game
// has exactly one active round at a time), 'past' when it had already
// closed by then (REQ-408, pre-drilled into `roundId` via
// PastRoundsLeaderboard's own `initialRoundId` prop below). Exported from
// here (not a separate lib module) since it's defined entirely in terms of
// this file's own `GameKey`/`Scope`.
export interface LeaderboardRoundTarget {
  gameKey: GameKey;
  scope: 'live' | 'past';
  roundId: string;
}

const GAME_TABS: Array<{ value: GameKey; label: string }> = [
  { value: XG_GRID_GAME_KEY, label: 'xG Grid' },
  { value: XG_PATH_GAME_KEY, label: 'xG Path' },
];

// SCREEN-03: this screen still only reads the global league — custom
// leagues (REQ-402/403/S-063) can now be created/joined via LeaguesScreen,
// but REQ-404's own "[My League ▾] [+ New]" tab switcher and per-league
// leaderboard reads here remain unbuilt (LeaguesScreen only lists a
// player's own leagues by name/code, no leaderboard rendering). REQ-406/
// 407/408 (S-053/S-054) add the scope selector above instead.
export function LeaderboardScreen({
  accessToken,
  onAuthError,
  initialGameKey,
  initialScope,
  initialRoundId,
}: LeaderboardScreenProps) {
  // REQ-1210/ADR-0082: `initialScope`/`initialGameKey` seed this screen's
  // own scope/game-tab state exactly once, at mount — every existing call
  // site (which passes neither) is unaffected, still defaulting to
  // 'all-time'/xG Grid.
  const [scope, setScope] = useState<Scope>(initialScope ?? 'all-time');
  // REQ-410/ADR-0043 (S-087): which game every scope below reads. Switching
  // this must re-fetch whichever scope is currently selected, scoped to the
  // new game — it must NOT reset `scope` itself back to 'all-time' (that
  // would silently discard the player's chosen view, and defeats the
  // purpose of the switcher sitting *above* the scope row rather than
  // resetting it).
  const [gameKey, setGameKey] = useState<GameKey>(initialGameKey ?? XG_GRID_GAME_KEY);
  // REQ-213 (S-068): independent of `scope`/every scope's own load state on
  // purpose — same reasoning as GridScreen.tsx's `explainerOpen` being
  // independent of `activeCell` (SCREEN-06's "doesn't discard in-progress
  // state" requirement). Opening/closing this never touches which scope tab
  // is selected, a loaded "Load more" page, or any scope's fetch state.
  // REQ-213 (2026-08-08): NOT independent of `gameKey`, unlike scope — see
  // the effect immediately below for why a game switch while this is open
  // closes it rather than leaving it open.
  const [explainerOpen, setExplainerOpen] = useState(false);

  // REQ-213 (2026-08-08): unlike a scope change (which never touches
  // `explainerOpen` — the explainer's content was, until today, identical
  // regardless of scope), a game switch changes *which explainer component
  // is even correct to show* — xG Grid's `ScoringExplainer` and xG Path's
  // `PathScoringExplainer` describe genuinely different rules (no shared
  // uniqueness/live-locked concepts). Rather than swapping the open modal's
  // content live under the player mid-read, or leaving the old game's
  // now-wrong content on screen, this follows the same "back out rather
  // than leave a stale, now-mismatched view up" precedent
  // `selectedRound`/`pastDetailState`'s own reset effect (now inside
  // `PastRoundsLeaderboard.tsx`) already establishes for a game switch —
  // close the modal; re-opening it via `(ⓘ)` shows the newly selected
  // game's correct content. Guarded by a ref (not a bare `gameKey`
  // dependency that also calls `setState` unconditionally) so it only
  // fires on a genuine change, never on mount.
  const prevGameKeyForExplainerRef = useRef<GameKey>(gameKey);
  useEffect(() => {
    if (prevGameKeyForExplainerRef.current !== gameKey) {
      setExplainerOpen(false);
    }
    prevGameKeyForExplainerRef.current = gameKey;
  }, [gameKey]);

  return (
    <div className="leaderboard-screen">
      <div className="leaderboard-screen__header">
        <div className="leaderboard-screen__title-row">
          <h2>Global leaderboard</h2>
          {/* REQ-213 (S-068, game-aware since 2026-08-08): opens
              SCREEN-06's general scoring/live-updates explainer for xG
              Grid, or SCREEN-10's xG Path explainer, whichever game's tab
              is currently selected (see the render call near the bottom of
              this component) — reachable regardless of which scope tab is
              active or whether that scope's data is loading, empty, or
              errored (it reads no scope/round state at all). */}
          <button
            type="button"
            className="leaderboard-screen__info-toggle"
            onClick={() => setExplainerOpen(true)}
            aria-label="How scoring works"
          >
            ⓘ
          </button>
        </div>
        {/* ADR-0021/design-document.md SCREEN-03: scored like golf — this
            corrects the natural "higher number = better" assumption before
            a player reads any rank. Must never be omitted or left implicit
            in the ranking order alone. Shown for every scope, since it's a
            property of every ranked list this screen can show. */}
        <p className="leaderboard-screen__subtitle">Lowest total wins</p>
      </div>
      {/* REQ-410/ADR-0043 (S-087): the game switcher — same plain
          underline-tab pattern as the scope tabs below (own class names
          since it's a visually distinct row, not the same DOM row), sitting
          above all four scope tabs since it scopes every one of them, not
          just "All-time". Selecting a tab here deliberately never touches
          `scope` — see each scope component's own effect for how the
          re-fetch under the new game happens without resetting the
          selected tab. */}
      <div className="leaderboard-screen__game-tabs" role="tablist" aria-label="Game">
        {GAME_TABS.map(({ value, label }) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={gameKey === value}
            className={`leaderboard-screen__game-tab ${gameKey === value ? 'leaderboard-screen__game-tab--active' : ''}`}
            onClick={() => setGameKey(value)}
          >
            {label}
          </button>
        ))}
      </div>
      {/* REQ-406/407/408: a new, separate scope selector — distinct from the
          not-yet-built custom-league tabs (design-document.md SCREEN-03). */}
      <div className="leaderboard-screen__scope-tabs" role="tablist" aria-label="Leaderboard scope">
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'all-time'}
          className={`leaderboard-screen__scope-tab ${scope === 'all-time' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('all-time')}
        >
          All-time
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'live'}
          className={`leaderboard-screen__scope-tab ${scope === 'live' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('live')}
        >
          Current Round
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'past'}
          className={`leaderboard-screen__scope-tab ${scope === 'past' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('past')}
        >
          Previous Rounds
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={scope === 'window'}
          className={`leaderboard-screen__scope-tab ${scope === 'window' ? 'leaderboard-screen__scope-tab--active' : ''}`}
          onClick={() => setScope('window')}
        >
          Time Windows
        </button>
      </div>
      {/* All four are always mounted — see each component's own `active`
          prop doc comment for why (the all-time scope's poll must keep
          running in the background regardless of the selected tab, and the
          live/past/window scopes need to survive a switch away and back
          without losing their loaded state/selection). Only the one
          matching `scope` renders non-null output. */}
      <AllTimeLeaderboard
        accessToken={accessToken}
        gameKey={gameKey}
        onAuthError={onAuthError}
        active={scope === 'all-time'}
      />
      <LiveLeaderboard
        accessToken={accessToken}
        gameKey={gameKey}
        onAuthError={onAuthError}
        active={scope === 'live'}
      />
      <PastRoundsLeaderboard
        accessToken={accessToken}
        gameKey={gameKey}
        onAuthError={onAuthError}
        active={scope === 'past'}
        // REQ-1210/ADR-0082: only meaningful the first time this scope
        // becomes active — PastRoundsLeaderboard consumes it once (its own
        // ref-guarded effect) and ignores it on any later, ordinary
        // re-entry, so this can safely stay set for the lifetime of this
        // component without re-triggering the jump.
        initialRoundId={initialRoundId}
      />
      <WindowedLeaderboard
        accessToken={accessToken}
        gameKey={gameKey}
        onAuthError={onAuthError}
        active={scope === 'window'}
      />
      {/* REQ-213 (2026-08-08): game-aware — xG Grid's explainer describes
          uniqueness/live-locked points and median ranking, none of which
          apply to xG Path (no uniqueness, no live/locked distinction, a
          different clue/attempt model), so each game gets its own
          component rather than one explainer branching its copy on
          `gameKey` internally (see PathScoringExplainer.tsx's own doc
          comment for why). The effect above closes this modal on a game
          switch, so by the time either branch below renders, `gameKey`
          always matches the content the player asked to see. */}
      {explainerOpen &&
        (gameKey === XG_GRID_GAME_KEY ? (
          <ScoringExplainer onClose={() => setExplainerOpen(false)} />
        ) : (
          <PathScoringExplainer onClose={() => setExplainerOpen(false)} />
        ))}
    </div>
  );
}
