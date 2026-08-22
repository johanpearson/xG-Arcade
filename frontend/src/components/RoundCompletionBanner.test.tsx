import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { RoundCompletionBanner } from './RoundCompletionBanner';

describe('RoundCompletionBanner', () => {
  it('REQ-1210: renders the caller-supplied points text verbatim (never inventing its own wording)', () => {
    render(
      <RoundCompletionBanner
        pointsText="~69 pts estimated"
        onViewLeaderboard={vi.fn()}
        onDismiss={vi.fn()}
      />,
    );
    expect(screen.getByText('~69 pts estimated')).toBeInTheDocument();
  });

  it('REQ-1210: plain "N pts" wording (xG Path) renders exactly as given, distinct from the estimated wording', () => {
    render(<RoundCompletionBanner pointsText="29 pts" onViewLeaderboard={vi.fn()} onDismiss={vi.fn()} />);
    expect(screen.getByText('29 pts')).toBeInTheDocument();
    expect(screen.queryByText(/estimated/)).not.toBeInTheDocument();
  });

  it('REQ-1210: activating the leaderboard link calls back out, and the points value/link are both present without any animation gate', async () => {
    const user = userEvent.setup();
    const onViewLeaderboard = vi.fn();
    render(
      <RoundCompletionBanner pointsText="12 pts" onViewLeaderboard={onViewLeaderboard} onDismiss={vi.fn()} />,
    );

    const link = screen.getByRole('button', { name: 'View leaderboard' });
    expect(link).toBeEnabled();
    await user.click(link);
    expect(onViewLeaderboard).toHaveBeenCalledTimes(1);
  });

  it('REQ-1210: the leaderboard link can be disabled while its destination is being confirmed', () => {
    render(
      <RoundCompletionBanner
        pointsText="12 pts"
        onViewLeaderboard={vi.fn()}
        onDismiss={vi.fn()}
        viewLeaderboardDisabled
      />,
    );
    expect(screen.getByRole('button', { name: 'View leaderboard' })).toBeDisabled();
  });

  it('dismissing calls back out without requiring the leaderboard link', async () => {
    const user = userEvent.setup();
    const onDismiss = vi.fn();
    render(<RoundCompletionBanner pointsText="12 pts" onViewLeaderboard={vi.fn()} onDismiss={onDismiss} />);

    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });
});
