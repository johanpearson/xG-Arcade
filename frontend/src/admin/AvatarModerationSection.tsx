import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { approveAvatarSubmission, fetchPendingAvatarSubmissions, rejectAvatarSubmission } from '../lib/admin';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import type { PendingAvatarSubmission } from '../lib/types';

interface AvatarModerationSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-517 (S-183): the avatar-moderation queue — reads S-181's
// GET /admin/avatar-submissions (oldest-first, Pending rows only; this UI
// never re-sorts or re-filters) and acts on a row via approve/reject.
// Rendered unconditionally in AdminScreen's "Users" group (REQ-516) — like
// AccountMetricsSection, never gated by the Non-Production-only activeRound
// probe UserDeletionSection/RoundControlSection share, since the endpoint is
// registered in every environment.
//
// Heading badge: "Avatar moderation (N)" mirrors UnverifiedDataSection's own
// "Unverified data (N)" heading convention, not PlayerSuggestionsEntry's
// button-label badge — this section renders its queue inline (like
// UnverifiedDataSection), it isn't a click-through entry point to another
// screen (unlike PlayerSuggestionsEntry/SuggestionsScreen). Count is derived
// from the same fetched list (`.length`), no second endpoint. Per REQ-512's
// "absence not a 0 badge" convention, a count of 0 omits the "(0)" suffix
// entirely — the section itself still renders, with an empty-state message,
// unlike PlayerSuggestionsEntry's button which never hides regardless of
// count.
export function AvatarModerationSection({ accessToken, onAuthError }: AvatarModerationSectionProps) {
  const fetchFn = useCallback(() => fetchPendingAvatarSubmissions(accessToken), [accessToken]);
  const { data: submissions, hidden, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });

  // Per-row action state, keyed by submission id. Kept here rather than
  // inside a per-row child component (unlike SuggestionsScreen's
  // PendingSuggestionRow/PlayerReviewPanel split) since approve/reject here
  // are single-shot actions with no multi-field review form to justify a
  // separate component — mirrors UnverifiedDataSection's own inline
  // per-row "Correct" form being folded into the same file.
  const [actingId, setActingId] = useState<string | null>(null);
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({});
  // A 409 (already resolved by another admin, a race — not a validation
  // error) is tracked separately from rowErrors so it can render its own
  // distinct message + "Refresh list" action, mirroring
  // SuggestionsScreen's PlayerReviewPanel 'conflict' phase rather than
  // looking like a random failure.
  const [conflictIds, setConflictIds] = useState<Set<string>>(new Set());

  // Drops any conflict/error state for a row that's no longer in the list
  // (e.g. after a refetch removes a resolved row) — same "don't let stale
  // per-row state linger" reasoning UnverifiedDataSection's selectedIds
  // effect already establishes.
  useEffect(() => {
    if (!submissions) return;
    const currentIds = new Set(submissions.map((row) => row.id));
    setConflictIds((prev) => {
      const filtered = new Set([...prev].filter((id) => currentIds.has(id)));
      return filtered.size === prev.size ? prev : filtered;
    });
    setRowErrors((prev) => {
      const entries = Object.entries(prev).filter(([id]) => currentIds.has(id));
      return entries.length === Object.keys(prev).length ? prev : Object.fromEntries(entries);
    });
  }, [submissions]);

  async function handleAction(id: string, action: 'approve' | 'reject') {
    setActingId(id);
    setRowErrors((prev) => {
      if (!(id in prev)) return prev;
      const { [id]: _removed, ...rest } = prev;
      return rest;
    });
    try {
      if (action === 'approve') {
        await approveAvatarSubmission(accessToken, id);
      } else {
        await rejectAvatarSubmission(accessToken, id);
      }
      await refetch();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      if (err instanceof ApiError && err.status === 409) {
        setConflictIds((prev) => new Set(prev).add(id));
        return;
      }
      setRowErrors((prev) => ({ ...prev, [id]: describeError(err) }));
    } finally {
      setActingId(null);
    }
  }

  if (hidden) return null;

  const count = submissions ? submissions.length : 0;

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Avatar moderation{count > 0 ? ` (${count})` : ''}</h3>

      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}

      {submissions === null && !loadError && <p className="admin-screen__empty">Loading avatar submissions…</p>}

      {submissions !== null && submissions.length === 0 && (
        <p className="admin-screen__empty">No pending avatar submissions to review.</p>
      )}

      {submissions !== null && submissions.length > 0 && (
        <ul className="admin-screen__list">
          {submissions.map((submission) => (
            <AvatarSubmissionRow
              key={submission.id}
              submission={submission}
              acting={actingId === submission.id}
              error={rowErrors[submission.id] ?? null}
              conflict={conflictIds.has(submission.id)}
              onApprove={() => handleAction(submission.id, 'approve')}
              onReject={() => handleAction(submission.id, 'reject')}
              onRefreshList={refetch}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

interface AvatarSubmissionRowProps {
  submission: PendingAvatarSubmission;
  acting: boolean;
  error: string | null;
  conflict: boolean;
  onApprove: () => void;
  onReject: () => void;
  onRefreshList: () => void;
}

// REQ-517: "every pending submission is listed with a preview of the
// uploaded image, the submitting player's DisplayName, and the submission
// time." submittingUserDisplayName's null-means-deleted fallback ("a deleted
// user") matches SuggestionsScreen's PendingSuggestionRow exactly, for
// consistency across the two admin review queues that share the same
// REQ-710 null-display-name case.
function AvatarSubmissionRow({
  submission,
  acting,
  error,
  conflict,
  onApprove,
  onReject,
  onRefreshList,
}: AvatarSubmissionRowProps) {
  return (
    <li className="admin-screen__row">
      <div className="admin-screen__avatar-row-summary">
        <img
          className="admin-screen__avatar-preview"
          src={submission.imagePreviewUrl}
          alt={`Submitted avatar from ${submission.submittingUserDisplayName ?? 'a deleted user'}`}
        />
        <div>
          <p className="admin-screen__row-summary">
            Submitted by {submission.submittingUserDisplayName ?? 'a deleted user'}
          </p>
          <p className="admin-screen__row-summary">{submission.createdAt}</p>
        </div>
      </div>

      {conflict ? (
        <div className="admin-screen__inline-form">
          <p className="admin-screen__error" role="alert">
            Already resolved by another admin — refresh to see the current state.
          </p>
          <button type="button" onClick={onRefreshList}>
            Refresh list
          </button>
        </div>
      ) : (
        <>
          {error && (
            <p className="admin-screen__error" role="alert">
              {error}
            </p>
          )}
          <div className="admin-screen__inline-form-actions">
            <button type="button" onClick={onApprove} disabled={acting}>
              {acting ? 'Working…' : 'Approve'}
            </button>
            <button type="button" onClick={onReject} disabled={acting}>
              {acting ? 'Working…' : 'Reject'}
            </button>
          </div>
        </>
      )}
    </li>
  );
}
