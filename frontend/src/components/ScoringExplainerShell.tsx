import { useEffect, useRef, type ReactNode } from 'react';

export interface ScoringExplainerShellProps {
  onClose: () => void;
  children: ReactNode;
  // Each game keeps its own existing CSS file/class names (ScoringExplainer.css,
  // PathScoringExplainer.css, PredictScoringExplainer.css) — this shell only
  // extracts the shared markup/behavior, not the styling, so none of the three
  // call sites' visual output changes.
  backdropClassName: string;
  dialogClassName: string;
  headerClassName: string;
  closeClassName: string;
}

// Shared "How scoring works" modal shell — extracted (quality-gate fix,
// S-198) once a third per-game explainer (PredictScoringExplainer) made this
// the same near-identical shell (focus management, Escape-to-close, dialog
// markup) duplicated a third time across ScoringExplainer.tsx/
// PathScoringExplainer.tsx/PredictScoringExplainer.tsx. PathScoringExplainer's
// own prior comment had already named "a third game needing the same shell"
// as the point to extract, not before — this is that extraction. Only the
// shell is shared — each game's actual scoring-rules content stays a separate
// component (genuinely different content per game, not duplicated).
export function ScoringExplainerShell({
  onClose,
  children,
  backdropClassName,
  dialogClassName,
  headerClassName,
  closeClassName,
}: ScoringExplainerShellProps) {
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  // A modal moves focus in on open and returns it to whatever triggered it
  // on close, rather than leaving a keyboard/screen-reader user's focus
  // stranded on a now-invisible element.
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    closeButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className={backdropClassName} onClick={onClose}>
      <div
        className={dialogClassName}
        role="dialog"
        aria-modal="true"
        aria-label="How scoring works"
        onClick={(event) => event.stopPropagation()}
      >
        <div className={headerClassName}>
          <h3>How scoring works</h3>
          <button
            ref={closeButtonRef}
            type="button"
            className={closeClassName}
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
