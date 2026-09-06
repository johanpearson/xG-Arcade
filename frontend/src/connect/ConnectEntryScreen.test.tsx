import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ConnectEntryScreen } from './ConnectEntryScreen';

// REQ-1415 / SCREEN-17: xG Connect's own lighter-weight front door — exactly
// two choices and a rules entry point, nothing that itself starts a match.
// App.test.tsx separately covers this wired into the real app (reaching it
// via GameSelectScreen's tile/HeaderNav's "Games" entry, and each choice
// actually navigating to FriendsScreen on the right tab); this file is the
// component's own dedicated suite.
describe('ConnectEntryScreen', () => {
  it('REQ-1415: renders exactly the two choices, plus a rules entry point, and nothing else that itself starts a match', () => {
    render(<ConnectEntryScreen onChallengeFriend={vi.fn()} onChallengeRandomPlayer={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Challenge a friend' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Challenge random player' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'How scoring works' })).toBeInTheDocument();

    // Exactly three interactive buttons total on this screen — the two
    // choices and the rules entry point, nothing more.
    expect(screen.getAllByRole('button')).toHaveLength(3);
  });

  it('REQ-1415: selecting "Challenge a friend" calls onChallengeFriend, not onChallengeRandomPlayer', async () => {
    const user = userEvent.setup();
    const onChallengeFriend = vi.fn();
    const onChallengeRandomPlayer = vi.fn();

    render(
      <ConnectEntryScreen onChallengeFriend={onChallengeFriend} onChallengeRandomPlayer={onChallengeRandomPlayer} />,
    );
    await user.click(screen.getByRole('button', { name: 'Challenge a friend' }));

    expect(onChallengeFriend).toHaveBeenCalledTimes(1);
    expect(onChallengeRandomPlayer).not.toHaveBeenCalled();
  });

  it('REQ-1415: selecting "Challenge random player" calls onChallengeRandomPlayer, not onChallengeFriend', async () => {
    const user = userEvent.setup();
    const onChallengeFriend = vi.fn();
    const onChallengeRandomPlayer = vi.fn();

    render(
      <ConnectEntryScreen onChallengeFriend={onChallengeFriend} onChallengeRandomPlayer={onChallengeRandomPlayer} />,
    );
    await user.click(screen.getByRole('button', { name: 'Challenge random player' }));

    expect(onChallengeRandomPlayer).toHaveBeenCalledTimes(1);
    expect(onChallengeFriend).not.toHaveBeenCalled();
  });

  // REQ-1416: the rules entry point is available here too, before any match
  // exists — a player deciding whether to try xG Connect can learn the
  // rules before committing.
  it('REQ-1416: activating the rules entry point opens the "How scoring works" dialog with xG Connect\'s own content', async () => {
    const user = userEvent.setup();
    render(<ConnectEntryScreen onChallengeFriend={vi.fn()} onChallengeRandomPlayer={vi.fn()} />);

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'How scoring works' }));

    const dialog = screen.getByRole('dialog', { name: 'How scoring works' });
    expect(dialog.textContent).toMatch(/target pick/i);
    expect(dialog.textContent).toMatch(/dispute/i);
  });

  it('REQ-1416: closing the dialog (Close button) returns to the entry screen with no dialog open', async () => {
    const user = userEvent.setup();
    render(<ConnectEntryScreen onChallengeFriend={vi.fn()} onChallengeRandomPlayer={vi.fn()} />);

    await user.click(screen.getByRole('button', { name: 'How scoring works' }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
