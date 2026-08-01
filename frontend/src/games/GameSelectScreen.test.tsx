import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { GameSelectScreen, XG_GRID_GAME_KEY, XG_PATH_GAME_KEY } from './GameSelectScreen';

// REQ-303 (S-021): the post-login game-selection landing screen.
describe('GameSelectScreen', () => {
  it('REQ-303: renders the xG Grid tile as the game to select', () => {
    render(<GameSelectScreen onSelectGame={vi.fn()} />);

    expect(screen.getByText('Choose a game')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'xG Grid' })).toBeInTheDocument();
  });

  it('REQ-303: selecting the xG Grid tile calls onSelectGame with the xG Grid game key', async () => {
    const user = userEvent.setup();
    const onSelectGame = vi.fn();

    render(<GameSelectScreen onSelectGame={onSelectGame} />);
    await user.click(screen.getByRole('button', { name: 'xG Grid' }));

    expect(onSelectGame).toHaveBeenCalledWith(XG_GRID_GAME_KEY);
    expect(onSelectGame).toHaveBeenCalledTimes(1);
  });
});

// REQ-303/S-085 (SCREEN-09): the second tile added for xG Path — both
// tiles render, in HeaderNav's "Games" list order (xG Grid first), and
// selecting the new tile reports the new game's own key.
describe('GameSelectScreen (S-085: xG Path tile)', () => {
  it('REQ-303: renders both the xG Grid and xG Path tiles, each with its name and one-line description visible', () => {
    render(<GameSelectScreen onSelectGame={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'xG Grid' })).toBeInTheDocument();
    expect(screen.getByText('Guess the player from two clues')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'xG Path' })).toBeInTheDocument();
    expect(screen.getByText('Guess the player from a revealed career')).toBeInTheDocument();
  });

  it('REQ-303: the xG Grid tile appears before the xG Path tile, matching HeaderNav\'s "Games" list order', () => {
    render(<GameSelectScreen onSelectGame={vi.fn()} />);

    const tileNames = screen
      .getAllByRole('button')
      .map((button) => button.getAttribute('aria-label'));

    expect(tileNames).toEqual(['xG Grid', 'xG Path']);
  });

  it('REQ-303: selecting the xG Path tile calls onSelectGame with the xG Path game key', async () => {
    const user = userEvent.setup();
    const onSelectGame = vi.fn();

    render(<GameSelectScreen onSelectGame={onSelectGame} />);
    await user.click(screen.getByRole('button', { name: 'xG Path' }));

    expect(onSelectGame).toHaveBeenCalledWith(XG_PATH_GAME_KEY);
    expect(onSelectGame).toHaveBeenCalledTimes(1);
  });
});
