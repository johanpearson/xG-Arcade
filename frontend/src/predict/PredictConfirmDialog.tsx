import { useEffect, useRef } from 'react';
import './PredictConfirmDialog.css';

export interface PredictConfirmDialogProps {
  onCancel: () => void;
  onConfirm: () => void;
  // REQ-1306: true while POST /predict/confirm is in flight — disables both
  // Cancel and Confirm (quality-gate fix, 2026-08-31: an in-flight request
  // is a real, if brief, race — letting Cancel through while the request is
  // still pending could close the dialog and mislead the player about
  // whether their predictions actually locked) and swaps the confirm
  // button's label, mirroring every other in-flight submit button in this
  // codebase (PathGuessInput's own "Submitting…" swap).
  confirming: boolean;
}

// REQ-1306: structural/accessibility pattern taken verbatim from
// frontend/src/nav/GuestLogoutConfirm.tsx — role="dialog", aria-modal,
// backdrop-click-to-close, Escape-to-close, focus-in-on-open/focus-return-
// on-close, Cancel focused by default (not the confirm action), same as
// that component and, per REQ-1306's own Test level note in
// requirements-document.md, the codebase's established precedent for a
// two-choice, cannot-undo confirmation. Cancelling calls `onCancel` only —
// no backend call is made from here, and PredictScreen.tsx's own onCancel
// leaves every prediction exactly as freely editable as before (REQ-1306's
// own "dismisses or cancels" acceptance criterion). Confirming calls
// `onConfirm`, which PredictScreen.tsx wires to the actual
// POST /predict/confirm call.
//
// Judgment call (flagged, see this story's own report / design-document.md
// SCREEN-14): GuestLogoutConfirm's confirm button is `accent-red` because
// that action is destructive (deletes a guest account and everything
// played). Confirming predictions destroys nothing — it locks in a value
// the player already chose — so this reuses the `accent-green-text` token
// PathGuessInput.tsx's own primary submit button already uses for a
// non-destructive primary action, not `accent-red`. Only the color token
// differs from GuestLogoutConfirm; every structural/accessibility choice
// above is identical.
export function PredictConfirmDialog({ onCancel, onConfirm, confirming }: PredictConfirmDialogProps) {
  const cancelButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onCancel();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onCancel]);

  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    cancelButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="predict-confirm-dialog-backdrop" onClick={onCancel}>
      <div
        className="predict-confirm-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="predict-confirm-dialog-title"
        data-testid="predict-confirm-dialog"
        onClick={(event) => event.stopPropagation()}
      >
        <h3 id="predict-confirm-dialog-title" className="predict-confirm-dialog__title">
          Confirm and lock your predictions?
        </h3>
        <p className="predict-confirm-dialog__text">
          Are you sure? You can&apos;t change your predictions after confirming.
        </p>
        <div className="predict-confirm-dialog__actions">
          <button
            ref={cancelButtonRef}
            type="button"
            className="predict-confirm-dialog__cancel"
            onClick={onCancel}
            disabled={confirming}
            data-testid="predict-confirm-dialog-cancel"
          >
            Cancel
          </button>
          <button
            type="button"
            className="predict-confirm-dialog__confirm"
            onClick={onConfirm}
            disabled={confirming}
            data-testid="predict-confirm-dialog-confirm"
          >
            {confirming ? 'Confirming…' : 'Confirm and lock'}
          </button>
        </div>
      </div>
    </div>
  );
}
