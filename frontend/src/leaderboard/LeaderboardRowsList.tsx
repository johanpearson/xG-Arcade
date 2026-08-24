import type { LeaderboardRow } from '../lib/types';

// ---- Shared row shape used by all four scopes' "ready" states -----------

export type RowsReadyState = {
  pages: LeaderboardRow[][];
  requestingUserRow: LeaderboardRow | null;
  nextCursor: number | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
};

// REQ-406/407/408 (ADR-0031's "never presented as if it were final/locked"
// rule): shared row/footer rendering for all four scopes, so the <li> row
// markup isn't quadruplicated (S-121 split this out of the single
// LeaderboardScreen.tsx it used to live in — AllTimeLeaderboard,
// LiveLeaderboard, PastRoundsLeaderboard, and WindowedLeaderboard all import
// it from here). `provisional` controls only the points text — a live total
// renders with the same "~N pts estimated" wording GridScreen.tsx/
// CellState.tsx already established for a single cell's live point value
// (S-018/REQ-204), applied per row here; a locked total (all-time or a past
// closed round) renders as plain "N pts", unchanged from before.
function formatPoints(points: number, provisional: boolean): string {
  return provisional ? `~${points} pts estimated` : `${points} pts`;
}

export function LeaderboardRowsList({
  rows,
  requestingUserRow,
  emptyMessage,
  hasMore,
  loadingMore,
  loadMoreError,
  onLoadMore,
  provisional,
  onSelectPlayer,
}: {
  rows: LeaderboardRow[];
  requestingUserRow: LeaderboardRow | null;
  emptyMessage: string;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreError: string | null;
  onLoadMore: () => void;
  provisional: boolean;
  // REQ-411 (S-179): opens SCREEN-13's stats/profile view for the row's
  // player when their display name is selected — optional (rather than
  // required) so every existing call site/test that predates this story
  // keeps rendering a plain, non-interactive name exactly as before; every
  // real call site (the four scope components, threaded up from
  // LeaderboardScreen/App.tsx) always passes it. Undefined here on purpose
  // never means "silently do nothing on click" — see the render below,
  // which only renders a <button> at all when this is provided.
  onSelectPlayer?: (userId: string, displayName: string) => void;
}) {
  // REQ-607: when the requesting user's row isn't among the currently
  // loaded rows (they're off-page, or — for the live scope — simply not a
  // participant), pin a distinct footer row with their real rank/points so
  // they always know their standing without loading more pages. When it IS
  // already visible in the list, skip the footer — showing both would be a
  // redundant duplicate.
  const showYouFooter =
    requestingUserRow !== null && !rows.some((row) => row.userId === requestingUserRow.userId);

  return (
    <>
      {rows.length === 0 ? (
        // design-document.md §5: "empty states are invitations."
        <p className="leaderboard-screen__empty">{emptyMessage}</p>
      ) : (
        <>
          <ol className="leaderboard-screen__list">
            {rows.map((row) => (
              <li
                key={row.userId}
                className={`leaderboard-screen__row ${row.isRequestingUser ? 'leaderboard-screen__row--you' : ''}`}
              >
                <span className="leaderboard-screen__rank mono-figure">{row.rank}</span>
                {/* REQ-411 (S-179): a real navigation target to SCREEN-13's
                    stats view, per REQ-411's "select that display name on
                    the leaderboard" acceptance criteria. Judgement call
                    (story text left this to discretion): EVERY row here —
                    including the requesting user's own row when it happens
                    to already be visible on the current page — becomes
                    clickable, rather than leaving just that one row as
                    inert plain text among an otherwise all-clickable list.
                    A partial list where most names are links but one
                    (unpredictably, whichever page the requester's own rank
                    happens to land on) isn't would read as broken/
                    inconsistent, not intentional — clicking your own name
                    here simply opens your own stats, which is harmless and
                    consistent with "view any player's stats the same way."
                    The separate "you" FOOTER below (a fixed, always-your-
                    own-row summary, never one of these in-list rows) is
                    deliberately left as plain text — it already unambiguously
                    means "you," so it doesn't need to become a link to
                    itself, and Settings already has a dedicated "My stats"
                    entry point for that same destination. */}
                {onSelectPlayer ? (
                  <button
                    type="button"
                    className="leaderboard-screen__name leaderboard-screen__name-button"
                    onClick={() => onSelectPlayer(row.userId, row.displayName)}
                  >
                    {row.displayName}
                  </button>
                ) : (
                  <span className="leaderboard-screen__name">{row.displayName}</span>
                )}
                <span className="leaderboard-screen__points mono-figure">
                  {formatPoints(row.totalPoints, provisional)}
                </span>
                {/* Text, not color-only (design-document.md §6). */}
                {row.isRequestingUser && <span className="leaderboard-screen__you-tag">you</span>}
              </li>
            ))}
          </ol>
          {hasMore && (
            <button
              type="button"
              className="leaderboard-screen__load-more"
              onClick={onLoadMore}
              disabled={loadingMore}
            >
              {loadingMore ? 'Loading more…' : 'Load more'}
            </button>
          )}
          {loadMoreError && <p className="leaderboard-screen__load-more-error">{loadMoreError}</p>}
        </>
      )}
      {showYouFooter && requestingUserRow && (
        <div className="leaderboard-screen__you-footer">
          <span className="leaderboard-screen__rank mono-figure">{requestingUserRow.rank}</span>
          <span className="leaderboard-screen__name">{requestingUserRow.displayName}</span>
          <span className="leaderboard-screen__points mono-figure">
            {formatPoints(requestingUserRow.totalPoints, provisional)}
          </span>
          {/* Text, not color-only (design-document.md §6). */}
          <span className="leaderboard-screen__you-tag">you</span>
        </div>
      )}
    </>
  );
}
