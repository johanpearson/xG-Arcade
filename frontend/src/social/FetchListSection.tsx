import type { ReactNode } from 'react';

export interface FetchListSectionProps<T> {
  data: T[] | null;
  loadError: string | null;
  emptyMessage: ReactNode;
  renderList: (data: T[]) => ReactNode;
}

// The "loading/error/empty/list" four-branch render shape (error text if
// the fetch failed; "Loading…" while `data` is still null and there's no
// error; an invitation/empty message once `data` resolved to zero rows; the
// real `<ul>` otherwise) was duplicated three times across `src/social/`
// (FriendsTab.tsx's two sections and ChallengesTab.tsx) before being
// flagged as a rule-of-three code-health-budget finding (ADR-0084) during
// S-217's quality gate — see LeaderboardRowsList.tsx for the same kind of
// extraction (shared *rendering*, not shared data-fetching) done once
// before in a sibling feature area, and useAuthedFetch.ts/useRoundFetch.ts
// for why this file deliberately owns only the render branch, not the
// fetch itself: `data`/`loadError` here are always whatever
// `useAuthedFetch` already produced, unchanged.
//
// Scoped to this directory's own `friends-screen__*` CSS classes rather
// than made fully generic across the app — LeaderboardRowsList.tsx already
// established that a shared render helper belongs beside the feature whose
// markup it standardizes, not in `lib/`, since a class-name-agnostic
// version isn't something any current caller outside `src/social/` needs.
export function FetchListSection<T>({ data, loadError, emptyMessage, renderList }: FetchListSectionProps<T>) {
  if (loadError) {
    return (
      <p className="friends-screen__error" role="alert">
        {loadError}
      </p>
    );
  }
  if (data === null) {
    return <p className="friends-screen__status">Loading…</p>;
  }
  if (data.length === 0) {
    return <p className="friends-screen__empty">{emptyMessage}</p>;
  }
  return <>{renderList(data)}</>;
}
