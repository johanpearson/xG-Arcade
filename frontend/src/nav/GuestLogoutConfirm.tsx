import { useEffect, useRef } from 'react';
import './GuestLogoutConfirm.css';

export interface GuestLogoutConfirmProps {
  onCancel: () => void;
  onConfirm: () => void;
}

// REQ-718 UI addendum (rule 4, 2026-08-01): shown only for a guest account
// (`isGuest === true`, App.tsx) when "Log out" is clicked — a guest's
// logout deletes the account (REQ-718 rule 1), so this component only ever
// gates *when* App.tsx's existing `handleLogout` fires, never how it
// behaves. Cancelling calls `onCancel` only — no token/session/screen
// change, no backend call is made from here. Confirming calls `onConfirm`,
// which App.tsx wires straight through to the existing, completely
// unmodified `handleLogout`. A non-guest account never mounts this
// component at all: App.tsx only opens it from the guest branch of its own
// "Log out" click handler.
//
// Structural/accessibility pattern taken from ScoringExplainer.tsx
// (SCREEN-06): role="dialog", aria-modal, backdrop-click-to-close,
// Escape-to-close, focus-in-on-open/focus-return-to-opener-on-close.
// Button treatment (Cancel / destructive-confirm, flex weighting) taken
// from DeleteAccountScreen.tsx's __cancel/__confirm pair instead of
// ScoringExplainer's single close button, since this is a two-choice
// confirmation, not an informational panel. Tokens only — no new color,
// typeface, or animation (see this story's own report for why no new
// design-document.md SCREEN entry was added: this reuses two
// already-documented patterns rather than introducing a third).
export function GuestLogoutConfirm({ onCancel, onConfirm }: GuestLogoutConfirmProps) {
  const cancelButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onCancel();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onCancel]);

  // Same focus-in/focus-return discipline as ScoringExplainer — moves focus
  // onto the dialog on open and returns it to whatever triggered it (the
  // header's "Log out" button) on close, rather than stranding a keyboard/
  // screen-reader user's focus on a now-invisible element. Focus starts on
  // Cancel (the non-destructive, "nothing happens" choice), not the
  // destructive confirm button, so a stray Enter press never deletes the
  // account by accident.
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    cancelButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="guest-logout-confirm-backdrop" onClick={onCancel}>
      <div
        className="guest-logout-confirm"
        role="dialog"
        aria-modal="true"
        aria-labelledby="guest-logout-confirm-title"
        data-testid="guest-logout-confirm"
        onClick={(event) => event.stopPropagation()}
      >
        <h3 id="guest-logout-confirm-title" className="guest-logout-confirm__title">
          Log out and delete guest account?
        </h3>
        <p className="guest-logout-confirm__text">
          You&apos;re playing as a guest. Logging out deletes this guest account and everything
          you&apos;ve played, right now — this can&apos;t be undone. Save your progress from
          Settings first if you want to keep it.
        </p>
        <div className="guest-logout-confirm__actions">
          <button
            ref={cancelButtonRef}
            type="button"
            className="guest-logout-confirm__cancel"
            onClick={onCancel}
            data-testid="guest-logout-confirm-cancel"
          >
            Cancel
          </button>
          <button
            type="button"
            className="guest-logout-confirm__confirm"
            onClick={onConfirm}
            data-testid="guest-logout-confirm-confirm"
          >
            Log out and delete account
          </button>
        </div>
      </div>
    </div>
  );
}
