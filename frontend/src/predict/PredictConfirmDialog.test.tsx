import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { PredictConfirmDialog } from './PredictConfirmDialog';

// REQ-1306: structural/accessibility pattern taken verbatim from
// frontend/src/nav/GuestLogoutConfirm.tsx — this is that component's own
// self-contained suite (App.test.tsx/PredictScreen.test.tsx cover it wired
// into the real confirm flow).
describe('PredictConfirmDialog', () => {
  it('REQ-1306: renders as a labeled, modal dialog with the required prompt text', () => {
    render(<PredictConfirmDialog onCancel={vi.fn()} onConfirm={vi.fn()} confirming={false} />);

    const dialog = screen.getByRole('dialog', { name: 'Confirm and lock your predictions?' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(dialog.textContent).toMatch(/Are you sure\? You can't change your predictions after confirming\./);
  });

  it('REQ-1306: focus starts on Cancel, not the confirm action, so a stray Enter never locks predictions by accident', () => {
    render(<PredictConfirmDialog onCancel={vi.fn()} onConfirm={vi.fn()} confirming={false} />);

    expect(screen.getByTestId('predict-confirm-dialog-cancel')).toHaveFocus();
  });

  it('REQ-1306: clicking Cancel calls onCancel only, never onConfirm', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    render(<PredictConfirmDialog onCancel={onCancel} onConfirm={onConfirm} confirming={false} />);

    await user.click(screen.getByTestId('predict-confirm-dialog-cancel'));

    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('REQ-1306: clicking the confirm button calls onConfirm only', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    render(<PredictConfirmDialog onCancel={onCancel} onConfirm={onConfirm} confirming={false} />);

    await user.click(screen.getByTestId('predict-confirm-dialog-confirm'));

    expect(onConfirm).toHaveBeenCalledTimes(1);
    expect(onCancel).not.toHaveBeenCalled();
  });

  it('REQ-1306: clicking the backdrop calls onCancel, same as an explicit Cancel click', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    render(<PredictConfirmDialog onCancel={onCancel} onConfirm={vi.fn()} confirming={false} />);

    await user.click(screen.getByTestId('predict-confirm-dialog').parentElement as HTMLElement);

    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('REQ-1306: clicking inside the dialog itself never triggers the backdrop close', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    render(<PredictConfirmDialog onCancel={onCancel} onConfirm={vi.fn()} confirming={false} />);

    await user.click(screen.getByTestId('predict-confirm-dialog'));

    expect(onCancel).not.toHaveBeenCalled();
  });

  it('REQ-1306: pressing Escape calls onCancel', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    render(<PredictConfirmDialog onCancel={onCancel} onConfirm={vi.fn()} confirming={false} />);

    await user.keyboard('{Escape}');

    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('REQ-1306: while confirming, both buttons are disabled and the confirm button reflects in-flight state', () => {
    render(<PredictConfirmDialog onCancel={vi.fn()} onConfirm={vi.fn()} confirming={true} />);

    expect(screen.getByTestId('predict-confirm-dialog-cancel')).toBeDisabled();
    const confirmButton = screen.getByTestId('predict-confirm-dialog-confirm');
    expect(confirmButton).toBeDisabled();
    expect(confirmButton).toHaveTextContent('Confirming…');
  });
});
