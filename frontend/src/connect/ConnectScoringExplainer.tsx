import { ScoringExplainerShell } from '../components/ScoringExplainerShell';
import './ConnectScoringExplainer.css';

export interface ConnectScoringExplainerProps {
  onClose: () => void;
}

// SCREEN-17 / REQ-1416: a player-facing "how xG Connect works" explainer,
// opened via a header (ⓘ) button on both the entry screen
// (ConnectEntryScreen.tsx, reachable before a match even exists) and the
// active-match chain-building screen (MatchScreen.tsx). A new, separate
// component rather than a game-aware branch inside ScoringExplainer.tsx/
// PathScoringExplainer.tsx/PredictScoringExplainer.tsx, for the same
// reasoning those siblings' own doc comments already give: xG Connect
// shares essentially none of its actual mechanics with those three games
// (no clue reveal, no uniqueness, no single-player round at all — a 1v1
// match with its own bust/timer/dispute rules instead). The modal shell
// itself (focus management, Escape-to-close, dialog markup, including the
// shared "How scoring works" heading/aria-label) comes from the existing
// ScoringExplainerShell (../components/), reused as-is rather than given a
// bespoke title — this content is broader than pure scoring (it also
// covers the bust rule, the forfeit timer, and the dispute mechanic), but
// every sibling explainer already blends "how the round/match plays" with
// "how it scores" under that same heading, so this keeps the same
// established label rather than fragmenting it per game.
//
// Content covers each of REQ-1416's five required points, verified against
// the actual acceptance criteria (not assumed): (1) how a match/chain
// works, (2) the two-strikes-per-step bust rule (REQ-1407), (3) scoring
// shape only, no exact formula (REQ-1408, matching REQ-213's own "no exact
// formula" precedent), (4) the 6-hour timer/forfeit (REQ-1405), and (5) the
// dispute mechanic (REQ-1412-1414), framed so a player learns disputing
// exists at all, not just that failures can occur.
export function ConnectScoringExplainer({ onClose }: ConnectScoringExplainerProps) {
  return (
    <ScoringExplainerShell
      onClose={onClose}
      backdropClassName="connect-scoring-explainer-backdrop"
      dialogClassName="connect-scoring-explainer"
      headerClassName="connect-scoring-explainer__header"
      closeClassName="connect-scoring-explainer__close"
    >
      <p className="connect-scoring-explainer__text">
        You and your opponent each privately pick one real football player as your own target
        pick. Once both picks are locked in, you each separately build a chain of real
        &ldquo;played together&rdquo; connections &mdash; players who shared a club at the same
        time &mdash; linking the two agreed target players together. You&apos;re connecting the
        two target players to each other, not your own pick directly to your opponent&apos;s.
      </p>
      <p className="connect-scoring-explainer__text">
        Each step in your chain can fail if the connection you name doesn&apos;t check out. The
        first failure at a given step is just a warning &mdash; you can try that step again. A
        second, consecutive failure at that same step ends your chain for this match.
      </p>
      <p className="connect-scoring-explainer__text">
        xG Arcade is scored like golf &mdash; lower is better. A completed chain scores better
        the fewer connections it took and the fewer failed first attempts you needed along the
        way.
      </p>
      <p className="connect-scoring-explainer__text">
        Each match has a 6-hour deadline starting from when it began. If you haven&apos;t
        completed your chain, or already busted, by then, you forfeit the match.
      </p>
      <p className="connect-scoring-explainer__text">
        If you believe a failed connection should actually have been accepted, you don&apos;t
        have to just take the failure as final &mdash; you can dispute that specific step
        instead of retrying it. Your opponent then reviews your dispute and decides whether it
        should have passed.
      </p>
    </ScoringExplainerShell>
  );
}
