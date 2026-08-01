import './GameSelectScreen.css';

// Tier 0 launched with exactly one game, so this key started as a
// client-side constant, not data from an endpoint — see docs/backlog.md
// S-021: a "list games" API would be building a catalog for a catalog of
// one. Still true for a catalog of two (S-085/SCREEN-09) — both keys below
// remain plain constants, matching the backend GameKey strings used
// throughout (e.g. RoundSchedulingOptionsResolver, PathGameModule).
export const XG_GRID_GAME_KEY = 'xg-grid';
export const XG_PATH_GAME_KEY = 'xg-path';

export interface GameSelectScreenProps {
  onSelectGame: (gameKey: string) => void;
}

// REQ-303 (S-021), extended by S-085/SCREEN-09 for a second game: shown
// immediately after login/signup, before either game's own screen.
// design-document.md's SCREEN-09 is the spec for the multi-tile layout
// below — tiles laid out in a row that wraps to stacked below 480px (same
// breakpoint HeaderNav.css's mobile toggle uses), tokens only
// (surface-card/border-hairline, no per-game accent color), order matching
// HeaderNav's "Games" list (xG Grid first, xG Path second — never
// alphabetical/recency), no loading state since both keys are client-side
// constants.
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
            tile/navigation to stay unchanged. */}
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Grid"
          onClick={() => onSelectGame(XG_GRID_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Grid</span>
          <span className="game-select-screen__tile-description">
            Guess the player from two clues
          </span>
        </button>
        <button
          type="button"
          className="game-select-screen__tile"
          aria-label="xG Path"
          onClick={() => onSelectGame(XG_PATH_GAME_KEY)}
        >
          <span className="game-select-screen__tile-name">xG Path</span>
          <span className="game-select-screen__tile-description">
            Guess the player from a revealed career
          </span>
        </button>
      </div>
    </div>
  );
}
