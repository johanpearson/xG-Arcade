import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import {
  commitPlayerSearch,
  commitSuggestion,
  fetchPendingSuggestions,
  lookupPlayerByName,
  lookupSuggestionPlayer,
  refreshPlayerFromWikidata,
  rejectSuggestion,
} from '../lib/admin';
import type {
  CommitPlayerDataPayload,
  CommitPlayerDataResult,
  PendingSuggestion,
  RefreshPlayerFromWikidataResponse,
  WikidataPlayerLookupResult,
} from '../lib/types';
import { PlayerRefreshFieldsList, describePlayerRefreshError } from './PlayerRefreshFieldsList';
import './SuggestionsScreen.css';

// REQ-509/510/S-129: turns a commit response's actually-written facts into a
// single plain-language sentence, shared by both PendingSuggestionRow's
// approval flow and ManualSearchSection's standalone flow so the two entry
// points never diverge in what they tell the admin about the same
// underlying write (see PlayerReviewPanel's own header comment on being the
// one shared component both entry points render). A genuine no-op
// (nothing created, no nationality write, no new clubs) is called out
// explicitly rather than folded into a generic "success" message — that
// ambiguity is exactly what S-129 exists to remove.
function describeCommitResult(result: CommitPlayerDataResult): string {
  const isNoOp = !result.playerCreated && !result.nationalityWritten && result.clubsAdded.length === 0;
  if (isNoOp) {
    return 'No changes — this data was already up to date.';
  }

  const parts: string[] = [];
  if (result.playerCreated) {
    parts.push('New player added.');
  }
  if (result.nationalityWritten) {
    parts.push(`Nationality set to ${result.nationality ?? '—'}.`);
  }
  if (result.clubsAdded.length > 0) {
    const label = result.clubsAdded.length === 1 ? 'club' : 'clubs';
    parts.push(`${result.clubsAdded.length} new ${label} added: ${result.clubsAdded.join(', ')}.`);
  }
  if (result.clubsAlreadyEffective.length > 0) {
    parts.push(`${result.clubsAlreadyEffective.join(', ')} already up to date.`);
  }
  return parts.join(' ');
}

export interface SuggestionsScreenProps {
  accessToken: string;
  onAuthError: () => void;
  onBackToAdmin: () => void;
}

type PageState =
  | { phase: 'loading' }
  | { phase: 'access-denied' }
  | { phase: 'error'; message: string }
  | { phase: 'ready' };

// REQ-509/REQ-510 (S-090)/ADR-0053: a new, standalone admin screen — never
// folded into AdminScreen.tsx (REQ-503's unrelated unverified-data queue).
// Reached only via AdminScreen's own "Player suggestions" link (mirroring
// how AdminScreen itself is only reachable from SettingsScreen, REQ-504) —
// there is no independent top-level nav entry. Follows AdminScreen's exact
// PageState/loading/401/403 shape rather than inventing a new one.
export function SuggestionsScreen({ accessToken, onAuthError, onBackToAdmin }: SuggestionsScreenProps) {
  const [pageState, setPageState] = useState<PageState>({ phase: 'loading' });
  const [suggestions, setSuggestions] = useState<PendingSuggestion[]>([]);
  const [openId, setOpenId] = useState<string | null>(null);
  // S-129: the confirmation message for the most recent commit, lifted up
  // here (rather than kept inside PendingSuggestionRow/PlayerReviewPanel)
  // because the row itself unmounts on every onDone — this is the only
  // place left standing to show it once the panel closes and the list
  // refetches. Mirrors ManualSearchSection's own `confirmation` state/
  // rendering pattern below rather than inventing a new one.
  const [confirmation, setConfirmation] = useState<string | null>(null);

  const refreshSuggestions = useCallback(async () => {
    const rows = await fetchPendingSuggestions(accessToken);
    setSuggestions(rows);
  }, [accessToken]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const rows = await fetchPendingSuggestions(accessToken);
        if (cancelled) return;
        setSuggestions(rows);
        setPageState({ phase: 'ready' });
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        setPageState({ phase: 'error', message: describeError(err) });
      }
    }

    load();

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  // Fires after a suggestion row's review panel commits, rejects, or hits a
  // 409 ("already resolved by someone else") — in every case the row is no
  // longer actionable, so the panel closes and the list is refetched rather
  // than patched in place (REQ-509's own "never left visibly actionable
  // after resolution" requirement). A refetch failure here is swallowed
  // deliberately: the underlying commit/reject/conflict has already
  // happened server-side by this point, so surfacing a fresh error would
  // misdescribe what went wrong — the stale row simply stays visible until
  // the admin's next successful refresh, and re-acting on it just re-runs
  // the same server-side checks.
  //
  // S-129: a successful commit also carries the actually-written response,
  // turned into a plain-language summary and shown above the list — the
  // row itself is gone by the time this renders, so this is the only place
  // an admin can see what a commit actually did.
  async function handleRowDone(reason: 'committed' | 'rejected' | 'refresh', result?: CommitPlayerDataResult) {
    setOpenId(null);
    setConfirmation(reason === 'committed' && result ? describeCommitResult(result) : null);
    try {
      await refreshSuggestions();
    } catch {
      // Best-effort — see comment above.
    }
  }

  if (pageState.phase === 'loading') {
    return <p className="suggestions-screen__status">Loading…</p>;
  }

  if (pageState.phase === 'access-denied') {
    return <p className="suggestions-screen__status">You don't have access to this page.</p>;
  }

  if (pageState.phase === 'error') {
    return <p className="suggestions-screen__status suggestions-screen__status--error">{pageState.message}</p>;
  }

  return (
    <div className="suggestions-screen">
      <div className="suggestions-screen__header">
        <h2 className="suggestions-screen__title">Player suggestions</h2>
        <button type="button" onClick={onBackToAdmin}>
          Back to admin
        </button>
      </div>

      <section className="suggestions-screen__section">
        <h3 className="suggestions-screen__section-title">Pending suggestions ({suggestions.length})</h3>
        {confirmation && <p className="suggestions-screen__confirmation">{confirmation}</p>}
        {suggestions.length === 0 ? (
          <p className="suggestions-screen__empty">No pending suggestions to review.</p>
        ) : (
          <ul className="suggestions-screen__list">
            {suggestions.map((suggestion) => (
              <PendingSuggestionRow
                key={suggestion.id}
                accessToken={accessToken}
                suggestion={suggestion}
                isOpen={openId === suggestion.id}
                onToggle={() => {
                  setConfirmation(null);
                  setOpenId((current) => (current === suggestion.id ? null : suggestion.id));
                }}
                onAuthError={onAuthError}
                onDone={handleRowDone}
              />
            ))}
          </ul>
        )}
      </section>

      <ManualSearchSection accessToken={accessToken} onAuthError={onAuthError} />
    </div>
  );
}

interface PendingSuggestionRowProps {
  accessToken: string;
  suggestion: PendingSuggestion;
  isOpen: boolean;
  onToggle: () => void;
  onAuthError: () => void;
  onDone: (reason: 'committed' | 'rejected' | 'refresh', result?: CommitPlayerDataResult) => void;
}

// REQ-509: "every pending suggestion is listed with the player name, the
// asserted club(s), the asserted nationality, the submitting user, and the
// submission timestamp." The row itself is always visible; "Review" opens
// the shared PlayerReviewPanel below it, bound to this suggestion's own
// lookup/commit/reject calls.
function PendingSuggestionRow({
  accessToken,
  suggestion,
  isOpen,
  onToggle,
  onAuthError,
  onDone,
}: PendingSuggestionRowProps) {
  // Stable across re-renders as long as accessToken/suggestion.id don't
  // change (they don't, for the lifetime of one row) — safe as a
  // PlayerReviewPanel effect dependency, never causing a refetch loop.
  const onLookup = useCallback(
    () => lookupSuggestionPlayer(accessToken, suggestion.id),
    [accessToken, suggestion.id],
  );
  const onCommit = useCallback(
    (payload: CommitPlayerDataPayload) => commitSuggestion(accessToken, suggestion.id, payload),
    [accessToken, suggestion.id],
  );
  const onReject = useCallback(() => rejectSuggestion(accessToken, suggestion.id), [accessToken, suggestion.id]);

  return (
    <li className="suggestions-screen__row">
      <div className="suggestions-screen__row-summary">
        <p className="suggestions-screen__row-player">{suggestion.playerName}</p>
        <p className="suggestions-screen__row-detail">
          Claimed clubs: {suggestion.assertedClubs.length > 0 ? suggestion.assertedClubs.join(', ') : '—'}
        </p>
        <p className="suggestions-screen__row-detail">
          Claimed nationality: {suggestion.assertedNationality || '—'}
        </p>
        <p className="suggestions-screen__row-detail">
          Submitted by {suggestion.submittingUserDisplayName ?? 'a deleted user'} · {suggestion.createdAt}
        </p>
      </div>

      {isOpen ? (
        <PlayerReviewPanel
          key={suggestion.id}
          accessToken={accessToken}
          onLookup={onLookup}
          onCommit={onCommit}
          onReject={onReject}
          claim={{ clubs: suggestion.assertedClubs, nationality: suggestion.assertedNationality }}
          onAuthError={onAuthError}
          onDone={onDone}
          onCancel={onToggle}
        />
      ) : (
        <button type="button" onClick={onToggle}>
          Review
        </button>
      )}
    </li>
  );
}

interface ManualSearchSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-510/ADR-0053: "a variant entry point... not a third view" — same
// PlayerReviewPanel as PendingSuggestionRow above, bound to the standalone
// /admin/player-search/* endpoints instead, with `claim: null` (no
// suggestion, nothing to compare the fetch against) and `onReject: null`
// (there is no suggestion row to reject). searchTarget's identity changes on
// every new search, which is what keys PlayerReviewPanel to remount and run
// a fresh lookup for the newly-typed name.
function ManualSearchSection({ accessToken, onAuthError }: ManualSearchSectionProps) {
  const [name, setName] = useState('');
  const [searchTarget, setSearchTarget] = useState<{ name: string; nonce: number } | null>(null);
  const [confirmation, setConfirmation] = useState<string | null>(null);

  function handleSearch(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) return;
    setConfirmation(null);
    setSearchTarget({ name: trimmed, nonce: Date.now() });
  }

  const onLookup = useCallback(() => {
    // searchTarget is always set by the time PlayerReviewPanel below is
    // rendered (it's only mounted when non-null) — this branch exists only
    // to satisfy the return type, never actually reached.
    if (!searchTarget) return Promise.reject(new Error('No search in progress.'));
    return lookupPlayerByName(accessToken, searchTarget.name);
  }, [accessToken, searchTarget]);

  const onCommit = useCallback(
    (payload: CommitPlayerDataPayload) => commitPlayerSearch(accessToken, payload),
    [accessToken],
  );

  function handleDone(reason: 'committed' | 'rejected' | 'refresh', result?: CommitPlayerDataResult) {
    setConfirmation(reason === 'committed' && result ? describeCommitResult(result) : null);
    setSearchTarget(null);
  }

  return (
    <section className="suggestions-screen__section">
      <h3 className="suggestions-screen__section-title">Search Wikidata directly</h3>
      <p className="suggestions-screen__hint">
        Look up and add a player's data without a submitted suggestion (REQ-510).
      </p>

      <form className="suggestions-screen__search-form" onSubmit={handleSearch}>
        <label className="suggestions-screen__field">
          <span>Player name</span>
          <input
            type="text"
            value={name}
            onChange={(event) => {
              setName(event.target.value);
              setConfirmation(null);
            }}
            placeholder="e.g. Kylian Mbappé"
          />
        </label>
        <button type="submit" disabled={!name.trim()}>
          Search
        </button>
      </form>

      {confirmation && <p className="suggestions-screen__confirmation">{confirmation}</p>}

      {searchTarget && (
        <PlayerReviewPanel
          key={searchTarget.nonce}
          accessToken={accessToken}
          onLookup={onLookup}
          onCommit={onCommit}
          onReject={null}
          claim={null}
          onAuthError={onAuthError}
          onDone={handleDone}
          onCancel={() => setSearchTarget(null)}
        />
      )}
    </section>
  );
}

type LookupPhase =
  | { phase: 'loading' }
  | { phase: 'found'; data: WikidataPlayerLookupResult }
  | { phase: 'not-found' }
  | { phase: 'unavailable' }
  | { phase: 'conflict' }
  | { phase: 'error'; message: string };

interface PlayerReviewPanelProps {
  // REQ-515: needed only for the inline "Refresh from Wikidata" action
  // below, which calls refreshPlayerFromWikidata directly — every other
  // call in this panel already goes through the caller-supplied
  // onLookup/onCommit/onReject callbacks instead of taking accessToken
  // itself, but there's no equivalent per-panel callback for REQ-513's
  // existing endpoint (it isn't scoped to a suggestion/search at all, just
  // a bare player id), so the token is threaded through directly here.
  accessToken: string;
  onLookup: () => Promise<WikidataPlayerLookupResult>;
  onCommit: (payload: CommitPlayerDataPayload) => Promise<CommitPlayerDataResult>;
  onReject: (() => Promise<void>) | null;
  claim: { clubs: string[]; nationality: string } | null;
  onAuthError: () => void;
  // 'committed'/'rejected' after a successful action; 'refresh' when the
  // admin explicitly asks to refresh after a 409/stale-row error — every
  // case means "this item is no longer actionable here," so the caller
  // treats them the same way (close + refetch) unless it cares about the
  // distinction. 'committed' always carries the commit response's `result`
  // (S-129) so both callers can build a real confirmation message via
  // `describeCommitResult` instead of a generic success string.
  onDone: (reason: 'committed' | 'rejected' | 'refresh', result?: CommitPlayerDataResult) => void;
  onCancel: () => void;
}

// REQ-509/REQ-510/ADR-0053: the one shared lookup → review → commit/reject
// component both entry points render — "a variant entry point... not a
// parallel reimplementation" applies to the frontend the same way the
// backend's single LookupPlayerAsync/CommitPlayerDataAsync helpers already
// apply it there (AdminSuggestionEndpoints.cs's own header comment). Runs
// exactly one lookup per mount (keyed by the caller via `key=`), so a new
// suggestion/search always gets a fresh panel instance rather than reusing
// stale state — no separate "reset" logic needed here.
function PlayerReviewPanel({
  accessToken,
  onLookup,
  onCommit,
  onReject,
  claim,
  onAuthError,
  onDone,
  onCancel,
}: PlayerReviewPanelProps) {
  const [attempt, setAttempt] = useState(0);
  const [phase, setPhase] = useState<LookupPhase>({ phase: 'loading' });

  const [fullName, setFullName] = useState('');
  const [nationality, setNationality] = useState('');
  const [clubsText, setClubsText] = useState('');
  const [reason, setReason] = useState('');
  const [wikidataQid, setWikidataQid] = useState<string | null>(null);

  const [committing, setCommitting] = useState(false);
  const [commitError, setCommitError] = useState<string | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [rejectError, setRejectError] = useState<string | null>(null);

  // REQ-515: the inline "Refresh from Wikidata" action's own in-flight/
  // error/result state — independent of commit/reject above, since it's a
  // separate, non-destructive action (REQ-513/514's own "no confirm step"
  // reasoning applies here unchanged) that can run without affecting the
  // commit form.
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [refreshResult, setRefreshResult] = useState<RefreshPlayerFromWikidataResponse | null>(null);

  // Runs on mount and on every explicit "Try again" click (the `attempt`
  // counter) — never on any other re-render, since onLookup/onAuthError are
  // stable references from the caller (see PendingSuggestionRow/
  // ManualSearchSection's own useCallback comments).
  useEffect(() => {
    let cancelled = false;
    setPhase({ phase: 'loading' });
    // REQ-515: a fresh lookup (mount, or "Try again" after an unavailable
    // result) always starts with a clean inline-refresh slate — never
    // carries a stale result/error over from a previous fetched player.
    setRefreshing(false);
    setRefreshError(null);
    setRefreshResult(null);

    onLookup()
      .then((result) => {
        if (cancelled) return;
        if (result.found) {
          setPhase({ phase: 'found', data: result });
          setFullName(result.fullName ?? '');
          setNationality(result.nationality ?? '');
          setClubsText(result.clubs.join('\n'));
          setWikidataQid(result.wikidataQid);
        } else {
          // REQ-509/ADR-0046: a normal, valid "no matching footballer on
          // Wikidata" outcome — never rendered as an error.
          setPhase({ phase: 'not-found' });
        }
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 503) {
          // REQ-509/ADR-0046: "lookup unavailable, try again" — must render
          // distinctly from the `found: false` no-match branch above; never
          // conflated with it.
          setPhase({ phase: 'unavailable' });
          return;
        }
        if (err instanceof ApiError && err.status === 409) {
          setPhase({ phase: 'conflict' });
          return;
        }
        setPhase({ phase: 'error', message: describeError(err) });
      });

    return () => {
      cancelled = true;
    };
  }, [attempt, onLookup, onAuthError]);

  async function handleCommit(event: FormEvent) {
    event.preventDefault();
    if (phase.phase !== 'found' || !wikidataQid) return;

    setCommitting(true);
    setCommitError(null);
    try {
      const clubs = clubsText
        .split('\n')
        .map((club) => club.trim())
        .filter((club) => club.length > 0);
      const payload: CommitPlayerDataPayload = {
        wikidataQid,
        fullName: fullName.trim(),
        nationality: nationality.trim() || null,
        clubs,
        reason: reason.trim(),
      };
      const result = await onCommit(payload);
      onDone('committed', result);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      if (err instanceof ApiError && err.status === 409) {
        setPhase({ phase: 'conflict' });
        return;
      }
      setCommitError(describeError(err));
    } finally {
      setCommitting(false);
    }
  }

  async function handleReject() {
    if (!onReject) return;
    setRejecting(true);
    setRejectError(null);
    try {
      await onReject();
      onDone('rejected');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      if (err instanceof ApiError && err.status === 409) {
        setPhase({ phase: 'conflict' });
        return;
      }
      setRejectError(describeError(err));
    } finally {
      setRejecting(false);
    }
  }

  // REQ-515: reuses REQ-513's existing refresh endpoint directly (same
  // refreshPlayerFromWikidata client function, no duplicate API call) and
  // the same 404/409/503/401 handling via the shared
  // describePlayerRefreshError helper. Only ever called with a non-null
  // `existingPlayerId` (the button that triggers this is only rendered when
  // one is present), so there's no "no player id" branch to guard here.
  async function handleInlineRefresh(existingPlayerId: string) {
    setRefreshing(true);
    setRefreshError(null);
    try {
      const response = await refreshPlayerFromWikidata(accessToken, existingPlayerId);
      setRefreshResult(response);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setRefreshError(describePlayerRefreshError(err));
    } finally {
      setRefreshing(false);
    }
  }

  if (phase.phase === 'loading') {
    return <p className="suggestions-screen__review-status">Looking up player on Wikidata…</p>;
  }

  if (phase.phase === 'unavailable') {
    return (
      <div className="suggestions-screen__review">
        <p className="suggestions-screen__error" role="alert">
          Lookup unavailable — we couldn't reach Wikidata to verify this player. Try again.
        </p>
        <div className="suggestions-screen__review-actions">
          <button type="button" onClick={() => setAttempt((current) => current + 1)}>
            Try again
          </button>
          <button type="button" onClick={onCancel}>
            Close
          </button>
        </div>
      </div>
    );
  }

  if (phase.phase === 'conflict') {
    return (
      <div className="suggestions-screen__review">
        <p className="suggestions-screen__hint">
          Already resolved by another admin since this list loaded. Refresh to see the current state.
        </p>
        <button type="button" onClick={() => onDone('refresh')}>
          Refresh list
        </button>
      </div>
    );
  }

  if (phase.phase === 'error') {
    return (
      <div className="suggestions-screen__review">
        <p className="suggestions-screen__error" role="alert">
          {phase.message}
        </p>
        <div className="suggestions-screen__review-actions">
          <button type="button" onClick={() => onDone('refresh')}>
            Refresh list
          </button>
          <button type="button" onClick={onCancel}>
            Close
          </button>
        </div>
      </div>
    );
  }

  if (phase.phase === 'not-found') {
    return (
      <div className="suggestions-screen__review">
        <p className="suggestions-screen__hint">
          Wikidata has no footballer matching this name — nothing fetched to confirm or commit.
        </p>
        {rejectError && (
          <p className="suggestions-screen__error" role="alert">
            {rejectError}
          </p>
        )}
        <div className="suggestions-screen__review-actions">
          {onReject && (
            <button type="button" onClick={handleReject} disabled={rejecting}>
              {rejecting ? 'Rejecting…' : 'Reject suggestion'}
            </button>
          )}
          <button type="button" onClick={onCancel} disabled={rejecting}>
            Close
          </button>
        </div>
      </div>
    );
  }

  // phase.phase === 'found'
  const { data } = phase;
  const existingPlayerId = data.existingPlayerId;
  const hasClubText = clubsText
    .split('\n')
    .some((club) => club.trim().length > 0);
  const hasNationalityText = nationality.trim().length > 0;
  // Reason is only ever persisted when a nationality is committed (written to
  // PlayerOverride.Reason for REQ-501 audit purposes) - PlayerAttribute has no
  // audit columns, so a clubs-only commit has nowhere to store it. See ADR-0060.
  const canCommit =
    fullName.trim().length > 0 &&
    (hasNationalityText || hasClubText) &&
    (!hasNationalityText || reason.trim().length > 0);

  return (
    <div className="suggestions-screen__review">
      <p className="suggestions-screen__row-detail">Wikidata ID: {wikidataQid ?? '—'}</p>

      {/* REQ-515: only rendered when the lookup's resolved wikidataQid
          already has a local Player row on file — a brand-new player being
          added has nothing yet to refresh. Reuses REQ-513's existing
          refresh endpoint (refreshPlayerFromWikidata) and REQ-514's own
          four-field changed/unchanged presentation (PlayerRefreshFieldsList)
          rather than a second copy of either. */}
      {existingPlayerId && (
        <div className="suggestions-screen__inline-refresh">
          <button
            type="button"
            onClick={() => handleInlineRefresh(existingPlayerId)}
            disabled={refreshing}
          >
            {refreshing ? 'Refreshing…' : 'Refresh from Wikidata'}
          </button>
          {refreshError && (
            <p className="suggestions-screen__error" role="alert">
              {refreshError}
            </p>
          )}
          {refreshResult && <PlayerRefreshFieldsList result={refreshResult} />}
        </div>
      )}

      {claim && (
        <div className="suggestions-screen__comparison">
          <div className="suggestions-screen__comparison-column">
            <h4 className="suggestions-screen__comparison-title">Suggested by player</h4>
            <p className="suggestions-screen__row-detail">
              Clubs: {claim.clubs.length > 0 ? claim.clubs.join(', ') : '—'}
            </p>
            <p className="suggestions-screen__row-detail">Nationality: {claim.nationality || '—'}</p>
          </div>
          <div className="suggestions-screen__comparison-column">
            <h4 className="suggestions-screen__comparison-title">Fetched from Wikidata</h4>
            <p className="suggestions-screen__row-detail">
              Clubs: {data.clubs.length > 0 ? data.clubs.join(', ') : '—'}
            </p>
            <p className="suggestions-screen__row-detail">Nationality: {data.nationality || '—'}</p>
          </div>
        </div>
      )}

      <form className="suggestions-screen__form" onSubmit={handleCommit}>
        <label className="suggestions-screen__field">
          <span>Full name</span>
          <input
            type="text"
            required
            value={fullName}
            onChange={(event) => setFullName(event.target.value)}
            disabled={committing}
          />
        </label>
        <label className="suggestions-screen__field">
          <span>Nationality</span>
          <input
            type="text"
            value={nationality}
            onChange={(event) => setNationality(event.target.value)}
            disabled={committing}
          />
        </label>
        <label className="suggestions-screen__field">
          <span>Clubs (one per line)</span>
          <textarea
            value={clubsText}
            onChange={(event) => setClubsText(event.target.value)}
            disabled={committing}
            rows={4}
          />
        </label>
        <label className="suggestions-screen__field">
          <span>Reason{hasNationalityText ? '' : ' (optional — clubs-only commits have nowhere to store it)'}</span>
          <input
            type="text"
            required={hasNationalityText}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            disabled={committing}
            placeholder="Why this data is confirmed correct"
          />
        </label>

        {!canCommit && (
          <p className="suggestions-screen__hint">
            {hasNationalityText
              ? 'Enter a reason before committing a nationality.'
              : 'Enter at least one of nationality or clubs before committing.'}
          </p>
        )}
        {commitError && (
          <p className="suggestions-screen__error" role="alert">
            {commitError}
          </p>
        )}
        {rejectError && (
          <p className="suggestions-screen__error" role="alert">
            {rejectError}
          </p>
        )}

        <div className="suggestions-screen__review-actions">
          <button type="submit" disabled={committing || rejecting || !canCommit}>
            {committing ? 'Committing…' : 'Commit'}
          </button>
          {onReject && (
            <button type="button" onClick={handleReject} disabled={committing || rejecting}>
              {rejecting ? 'Rejecting…' : 'Reject'}
            </button>
          )}
          <button type="button" onClick={onCancel} disabled={committing || rejecting}>
            Cancel
          </button>
        </div>
      </form>
    </div>
  );
}
