import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { SplashScreen } from './SplashScreen';

// REQ-719: the unauthenticated landing screen shown before AuthScreen.
describe('SplashScreen', () => {
  it('REQ-719: shows the "xG Arcade" name with clear visual presence and a single call-to-action', () => {
    render(<SplashScreen onGetStarted={vi.fn()} />);

    expect(screen.getByRole('heading', { name: 'xG Arcade' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log in or sign up' })).toBeInTheDocument();
  });

  it('REQ-719: activating the call-to-action invokes onGetStarted', async () => {
    const user = userEvent.setup();
    const onGetStarted = vi.fn();

    render(<SplashScreen onGetStarted={onGetStarted} />);
    await user.click(screen.getByRole('button', { name: 'Log in or sign up' }));

    expect(onGetStarted).toHaveBeenCalledTimes(1);
  });
});
