import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ConnectScoringExplainer } from './ConnectScoringExplainer';

// REQ-1416 / SCREEN-17: xG Connect's own "how it works" explainer, built on
// the shared ScoringExplainerShell (../components/ScoringExplainerShell.tsx)
// — same self-contained-suite convention as ScoringExplainer.test.tsx
// (mounted directly via its own onClose prop; ConnectEntryScreen.test.tsx/
// MatchScreen.test.tsx separately cover each entry point wiring into this).
describe('ConnectScoringExplainer', () => {
  it('REQ-1416: renders as a labeled, modal dialog', () => {
    render(<ConnectScoringExplainer onClose={vi.fn()} />);

    const dialog = screen.getByRole('dialog', { name: 'How scoring works' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  it('REQ-1416: contains text covering point 1 — a match works by each player privately picking a target, then both building a chain of real shared-club connections linking the two target players (not connecting their own pick to the opponent\'s)', () => {
    render(<ConnectScoringExplainer onClose={vi.fn()} />);
    const dialog = screen.getByRole('dialog');

    expect(dialog.textContent).toMatch(/target pick/i);
    expect(dialog.textContent).toMatch(/shared a club at the same/i);
    expect(dialog.textContent).toMatch(/linking the two agreed target players/i);
    expect(dialog.textContent).toMatch(/not your own pick directly to your opponent/i);
  });

  it('REQ-1416: contains text covering point 2 — the two-strikes-per-step bust rule (REQ-1407)', () => {
    render(<ConnectScoringExplainer onClose={vi.fn()} />);
    const dialog = screen.getByRole('dialog');

    expect(dialog.textContent).toMatch(/first failure.*is just a warning/i);
    expect(dialog.textContent).toMatch(/second, consecutive failure/i);
    expect(dialog.textContent).toMatch(/ends your chain/i);
  });

  it('REQ-1416: contains text covering point 3 — scoring shape (golf-style, fewer connections and fewer failed first attempts is better), with no exact formula stated', () => {
    render(<ConnectScoringExplainer onClose={vi.fn()} />);
    const dialog = screen.getByRole('dialog');

    expect(dialog.textContent).toMatch(/golf/i);
    expect(dialog.textContent).toMatch(/lower is better/i);
    expect(dialog.textContent).toMatch(/fewer connections/i);
    expect(dialog.textContent).toMatch(/fewer failed first attempts/i);
    // No exact formula (REQ-213's own precedent) — no bare "x / y" or "pts"
    // figure appears anywhere in this content.
    expect(dialog.textContent).not.toMatch(/\bpts\b/i);
  });

  it('REQ-1416: contains text covering point 4 — the 6-hour timer and forfeit (REQ-1405)', () => {
    render(<ConnectScoringExplainer onClose={vi.fn()} />);
    const dialog = screen.getByRole('dialog');

    expect(dialog.textContent).toMatch(/6-hour deadline/i);
    expect(dialog.textContent).toMatch(/forfeit/i);
  });

  it('REQ-1416: contains text covering point 5 — the dispute mechanic (REQ-1412-1414), stated clearly enough that a player learns disputing exists', () => {
    render(<ConnectScoringExplainer onClose={vi.fn()} />);
    const dialog = screen.getByRole('dialog');

    expect(dialog.textContent).toMatch(/dispute/i);
    expect(dialog.textContent).toMatch(/opponent then reviews/i);
  });

  it('REQ-1416: the dialog card has a bounded max-height and overflow-y: auto, so content that exceeds the viewport scrolls within the card instead of overflowing off-screen', () => {
    const { container } = render(<ConnectScoringExplainer onClose={vi.fn()} />);

    const card = container.querySelector('.connect-scoring-explainer') as HTMLElement;
    const style = getComputedStyle(card);

    expect(style.overflowY).toBe('auto');
    expect(style.maxHeight).not.toBe('');
    expect(style.maxHeight).not.toBe('none');
  });

  it('REQ-1416: clicking the backdrop calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const { container } = render(<ConnectScoringExplainer onClose={onClose} />);

    const backdrop = container.querySelector('.connect-scoring-explainer-backdrop');
    expect(backdrop).not.toBeNull();
    await user.click(backdrop as Element);

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-1416: clicking inside the dialog itself does not call onClose (only the backdrop does)', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<ConnectScoringExplainer onClose={onClose} />);

    await user.click(screen.getByRole('dialog'));

    expect(onClose).not.toHaveBeenCalled();
  });

  it('REQ-1416: clicking the [×] close button calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<ConnectScoringExplainer onClose={onClose} />);

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-1416: pressing Escape calls onClose', () => {
    const onClose = vi.fn();
    render(<ConnectScoringExplainer onClose={onClose} />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-1416: mounting moves focus into the dialog, and unmounting restores focus to whatever was focused before it mounted', () => {
    function Harness({ open }: { open: boolean }) {
      return (
        <div>
          <button type="button">How scoring works</button>
          {open && <ConnectScoringExplainer onClose={vi.fn()} />}
        </div>
      );
    }
    const { rerender } = render(<Harness open={false} />);
    const openButton = screen.getByRole('button', { name: 'How scoring works' });
    openButton.focus();
    expect(openButton).toHaveFocus();

    rerender(<Harness open />);
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();

    const restoreFocusSpy = vi.spyOn(openButton, 'focus');
    rerender(<Harness open={false} />);
    expect(restoreFocusSpy).toHaveBeenCalled();
  });
});
