import './GameSelectScreen.css';

// Tier 0 has exactly one game, so this key is a client-side constant, not
// data from an endpoint — see docs/backlog.md S-021: a "list games" API
// would be building a catalog for a catalog of one.
export const XG_GRID_GAME_KEY = 'xg-grid';

export interface GameSelectScreenProps {
  onSelectGame: (gameKey: string) => void;
}

// REQ-303 (S-021): shown immediately after login/signup, before the grid.
// No SCREEN-xx spec exists for this in design-document.md yet — same gap
// AuthScreen.tsx flagged for the login screen; built with only the existing
// §2 token system, no new ad-hoc values.
export function GameSelectScreen({ onSelectGame }: GameSelectScreenProps) {
  return (
    <div className="game-select-screen">
      <h2>Choose a game</h2>
      <button
        type="button"
        className="game-select-screen__tile"
        onClick={() => onSelectGame(XG_GRID_GAME_KEY)}
      >
        xG Grid
      </button>
    </div>
  );
}
