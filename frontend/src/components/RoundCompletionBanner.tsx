import './RoundCompletionBanner.css';

// REQ-1210/ADR-0082/design-document.md SCREEN-12: the generic round-
// completion banner shown by both GridScreen.tsx and PathScreen.tsx once
// `useCompletionTransition` (lib/roundCompletion.ts) reports a genuine
// in-session completion. Deliberately game-agnostic — it renders whatever
// `pointsText` its caller hands it (each game supplies its own already-
// established wording convention, see that prop's own doc comment) and
// calls back out for both of its actions rather than knowing anything about
// rounds, cells, puzzles, or leaderboard scopes itself.
export interface RoundCompletionBannerProps {
  // Pre-formatted by the caller so this component never invents a third
  // points-wording convention (REQ-1210's explicit constraint): xG Grid
  // passes "~N pts estimated" (REQ-204/213's existing provisional framing —
  // another player's still-open guess on a shared cell can still change
  // this total until the round closes), xG Path passes plain "N pts"
  // (REQ-1206 — a locked xG Path puzzle's points never change afterward).
  pointsText: string;
  onViewLeaderboard: () => void;
  // True only for the brief window where the caller is confirming whether
  // the completed round has since closed (REQ-1210's live-vs-past routing
  // decision) — disables the button so a fast double-click can't fire two
  // navigations, never hides it (the points value and the link itself are
  // still shown immediately, per REQ-1210's reduced-motion/"never gated"
  // requirement — this is a separate, momentary affordance, not a gate).
  viewLeaderboardDisabled?: boolean;
  onDismiss: () => void;
}

export function RoundCompletionBanner({
  pointsText,
  onViewLeaderboard,
  viewLeaderboardDisabled = false,
  onDismiss,
}: RoundCompletionBannerProps) {
  return (
    // role="status": a polite live-region announcement, not a modal dialog
    // — this deliberately never blocks or backdrops the rest of the screen
    // (see design-document.md SCREEN-12's own "not a blocking modal" note)
    // so it can never intercept a click meant for the header nav or any
    // other on-screen control.
    <div className="round-completion-banner round-completion-banner--animate-in" role="status">
      <div className="round-completion-banner__body">
        <p className="round-completion-banner__heading">Round complete</p>
        <p className="round-completion-banner__points mono-figure">{pointsText}</p>
      </div>
      <div className="round-completion-banner__actions">
        <button
          type="button"
          className="round-completion-banner__leaderboard-link"
          onClick={onViewLeaderboard}
          disabled={viewLeaderboardDisabled}
        >
          View leaderboard
        </button>
        <button
          type="button"
          className="round-completion-banner__dismiss"
          onClick={onDismiss}
          aria-label="Dismiss"
        >
          ×
        </button>
      </div>
    </div>
  );
}
