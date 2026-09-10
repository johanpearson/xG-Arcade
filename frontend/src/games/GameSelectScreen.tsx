import './GameSelectScreen.css';

// Tier 0 launched with exactly one game, so this key started as a
// client-side constant, not data from an endpoint — see docs/backlog.md
// S-021: a "list games" API would be building a catalog for a catalog of
// one. Still true for a catalog of five (xG Higher/Lower) — all keys below
// remain plain constants, matching the backend GameKey strings used
// throughout (e.g. RoundSchedulingOptionsResolver, PathGameModule,
// XGPredictGameModule, XGHigherLowerGameModule).
// `as const` (quality-gate follow-up, S-085) keeps these as literal types
// rather than widening to `string`, so onSelectGame's parameter type below
// can be the exact five-member union — a sixth, unhandled key becomes a
// compile error at any switch/if-chain over it, not a silent runtime no-op.
export const XG_GRID_GAME_KEY = 'xg-grid' as const;
export const XG_PATH_GAME_KEY = 'xg-path' as const;
// REQ-1301 (xG Predict): matches the backend GameKey string exactly
// ("xg-predict" — see PredictTemplateResolver/XGPredictGameModule).
export const XG_PREDICT_GAME_KEY = 'xg-predict' as const;
// REQ-1415 (xG Connect): matches the backend GameKey string exactly
// ("xg-connect" — see XGConnectGameModule.XGConnectGameKey).
export const XG_CONNECT_GAME_KEY = 'xg-connect' as const;
// REQ-1504/1505 (xG Higher/Lower, S-228): matches the backend GameKey
// string exactly ("xg-higher-lower" — see
// XGHigherLowerGameModule.XGHigherLowerGameKey).
export const XG_HIGHER_LOWER_GAME_KEY = 'xg-higher-lower' as const;

export interface GameSelectScreenProps {
  onSelectGame: (
    gameKey:
      | typeof XG_GRID_GAME_KEY
      | typeof XG_PATH_GAME_KEY
      | typeof XG_PREDICT_GAME_KEY
      | typeof XG_CONNECT_GAME_KEY
      | typeof XG_HIGHER_LOWER_GAME_KEY,
  ) => void;
}

// REQ-303 (S-021), extended by S-085/SCREEN-09 for a second game, again for
// xG Predict as a third, again for xG Connect as a fourth (REQ-1415), and
// again for xG Higher/Lower as a fifth (REQ-1504/1505, S-228): shown
// immediately after login/signup, before any game's own screen.
// design-document.md's SCREEN-09 is the spec for the multi-tile layout
// below — tiles laid out in a row that wraps to stacked below 480px (same
// breakpoint HeaderNav.css's mobile toggle uses), tokens only
// (surface-card/border-hairline, no per-game accent color), order matching
// HeaderNav's "Games" list (xG Grid first, xG Path second, xG Predict
// third, xG Connect fourth, xG Higher/Lower fifth — never
// alphabetical/recency), no loading state since all five keys are
// client-side constants.
//
// xG Connect's tile is a deliberate exception to every other tile's
// behavior (SCREEN-09's own 2026-09-06 status note, REQ-1415): selecting it
// does NOT navigate directly into that game's own play screen the way the
// other three tiles do — the caller (App.tsx) instead routes to a new
// two-choice entry screen ("Challenge a friend" / "Challenge random
// player"), since xG Connect has no single "start playing" action the way
// a solo round does. This component itself doesn't know that — it still
// just calls onSelectGame(XG_CONNECT_GAME_KEY) like every other tile; the
// branching lives entirely in App.tsx's switch.
export function GameSelectScreen({ onSelectGame }: GameSelectScreenProps) {
  return (
    <div className="game-select-screen">
      <h2>Choose a game</h2>
      <div className="game-select-screen__tiles">
        {/* aria-label pins the accessible name to just the game name,
            unaffected by the visible description span alongside it —
            existing `getByRole('button', { name: 'xG Grid' })` queries
            (App.test.tsx, GameSelectScreen.test.tsx) rely on that exact
            name, and S-085's own accept criterion requires the existing
            tile/navigation to stay unchanged. aria-describedby (quality-gate
            follow-up, S-085) then exposes the description span as the
            button's accessible *description* rather than dropping it
            entirely — SCREEN-09 treats that line as real content, not
            decoration, so assistive tech still needs to reach it. */}
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Grid"
          aria-describedby="game-tile-grid-desc"
          onClick={() => onSelectGame(XG_GRID_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Grid</span>
          <span id="game-tile-grid-desc" className="game-select-screen__tile-description">
            Guess the player from two clues
          </span>
        </button>
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Path"
          aria-describedby="game-tile-path-desc"
          onClick={() => onSelectGame(XG_PATH_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Path</span>
          <span id="game-tile-path-desc" className="game-select-screen__tile-description">
            Guess the player from a revealed career
          </span>
        </button>
        {/* REQ-1301: the third tile, positioned last — keeps this list and
            HeaderNav's own "Games" list order in agreement (never
            alphabetical/recency), same reasoning as the xG Path tile's own
            comment above. */}
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Predict"
          aria-describedby="game-tile-predict-desc"
          onClick={() => onSelectGame(XG_PREDICT_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Predict</span>
          <span id="game-tile-predict-desc" className="game-select-screen__tile-description">
            Predict the final score
          </span>
        </button>
        {/* REQ-1415: the fourth tile, positioned last — keeps this list and
            HeaderNav's own "Games" list order in agreement (never
            alphabetical/recency), same reasoning as the xG Path/xG Predict
            tiles' own comments above. Selecting it does not start a game
            directly — see this file's own top-of-file comment on why. */}
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Connect"
          aria-describedby="game-tile-connect-desc"
          onClick={() => onSelectGame(XG_CONNECT_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Connect</span>
          <span id="game-tile-connect-desc" className="game-select-screen__tile-description">
            Challenge a friend or a random player to connect two players
          </span>
        </button>
        {/* REQ-1504/1505 (S-228): the fifth tile, positioned last — keeps
            this list and HeaderNav's own "Games" list order in agreement
            (never alphabetical/recency), same reasoning as every earlier
            tile's own comment above. */}
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Higher/Lower"
          aria-describedby="game-tile-higher-lower-desc"
          onClick={() => onSelectGame(XG_HIGHER_LOWER_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Higher/Lower</span>
          <span id="game-tile-higher-lower-desc" className="game-select-screen__tile-description">
            Guess whether the next player is higher or lower
          </span>
        </button>
      </div>
    </div>
  );
}
