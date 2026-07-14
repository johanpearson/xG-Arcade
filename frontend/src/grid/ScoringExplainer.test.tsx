import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ScoringExplainer } from './ScoringExplainer';

// REQ-213 / SCREEN-06: a general, never cell-specific explainer of how the
// live vs. locked point value and golf-style scoring work, opened from the
// grid screen's header entry point (GridScreen.test.tsx covers that
// integration). This file covers ScoringExplainer's own self-contained
// behavior via its onClose prop.
describe('ScoringExplainer', () => {
  it('REQ-213: renders as a labeled dialog', () => {
    render(<ScoringExplainer onClose={vi.fn()} />);

    const dialog = screen.getByRole('dialog', { name: 'How scoring works' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  it('REQ-213: contains text covering all three required content points — a live estimate can change until round close, a locked/final value does not change after that, and xG Arcade is scored like golf (lower is better, less-commonly-guessed answers score better)', () => {
    render(<ScoringExplainer onClose={vi.fn()} />);

    const dialog = screen.getByRole('dialog');

    // Presence checks against required concepts, not exact wording
    // (REQ-213's own "Test level" note).
    expect(dialog.textContent).toMatch(/live estimate/i);
    expect(dialog.textContent).toMatch(/change/i);
    expect(dialog.textContent).toMatch(/round closes/i);
    expect(dialog.textContent).toMatch(/locked/i);
    expect(dialog.textContent).toMatch(/won't change again|does not change/i);
    expect(dialog.textContent).toMatch(/golf/i);
    expect(dialog.textContent).toMatch(/lower is better/i);
    expect(dialog.textContent).toMatch(/fewer other players|fewer other/i);

    // Never cell-specific — no raw numbers/percentages tied to any
    // particular guess.
    expect(dialog.textContent).not.toMatch(/\d+%/);
  });

  it('REQ-213: clicking the backdrop calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const { container } = render(<ScoringExplainer onClose={onClose} />);

    const backdrop = container.querySelector('.scoring-explainer-backdrop');
    expect(backdrop).not.toBeNull();
    await user.click(backdrop as Element);

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-213: clicking inside the dialog itself does not call onClose (only the backdrop does)', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<ScoringExplainer onClose={onClose} />);

    await user.click(screen.getByRole('dialog'));

    expect(onClose).not.toHaveBeenCalled();
  });

  it('REQ-213: clicking the [×] close button calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<ScoringExplainer onClose={onClose} />);

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-213: pressing Escape calls onClose', () => {
    const onClose = vi.fn();
    render(<ScoringExplainer onClose={onClose} />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  // design-document.md SCREEN-06: unlike GuessInput, this modal moves focus
  // in on open and returns it to whatever triggered it on close, rather
  // than leaving keyboard/screen-reader focus stranded on a now-invisible
  // element (a real gap found by a code-reviewer pass on this story's own
  // diff, which had claimed this already matched GuessInput's behavior when
  // it didn't).
  it('REQ-213: mounting moves focus into the dialog, and unmounting (as GridScreen does on close) restores focus to whatever was focused before it mounted', () => {
    function Harness({ open }: { open: boolean }) {
      return (
        <div>
          <button type="button">How scoring works</button>
          {open && <ScoringExplainer onClose={vi.fn()} />}
        </div>
      );
    }
    const { rerender } = render(<Harness open={false} />);
    const openButton = screen.getByRole('button', { name: 'How scoring works' });
    openButton.focus();
    expect(openButton).toHaveFocus();

    rerender(<Harness open />);
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();

    // jsdom's own post-detach focus target (document.body) isn't a
    // reliable thing to assert on across environments — what actually
    // matters, and what ScoringExplainer's cleanup effect is responsible
    // for, is that it explicitly calls .focus() on the originally-focused
    // element.
    const restoreFocusSpy = vi.spyOn(openButton, 'focus');
    rerender(<Harness open={false} />);
    expect(restoreFocusSpy).toHaveBeenCalled();
  });
});
