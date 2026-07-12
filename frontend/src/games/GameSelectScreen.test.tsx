import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { GameSelectScreen, XG_GRID_GAME_KEY } from './GameSelectScreen';

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
