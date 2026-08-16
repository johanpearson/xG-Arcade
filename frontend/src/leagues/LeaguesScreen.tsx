import { useCallback, useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { createLeague, fetchMyLeagues, joinLeague } from '../lib/leagues';
import type { CustomLeague } from '../lib/types';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import './LeaguesScreen.css';

export interface LeaguesScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

const NAME_MAX_LENGTH = 50;

// REQ-402/403: create a custom league and join one via invite code, plus a
// simple list of the caller's own custom leagues. Deliberately does NOT
// render a per-league leaderboard — that's REQ-404's separate, larger,
// tracked follow-up (a full per-custom-league leaderboard with tab
// switching); this screen's whole scope is create/join/list, matching the
// "simple list is enough" story boundary. The initial fetch-on-mount uses
// the shared `useAuthedFetch` hook (S-120) rather than a hand-rolled
// `cancelled`-flag `useEffect`: loading is `data === null`, ready is
// `data !== null`, and error is `loadError` set. `hidden` is never true for
// this endpoint (no 403 case here), so it's folded into the same inline
// error branch as `loadError` rather than getting its own render branch —
// there's nothing meaningful to show differently for a state that can't
// occur. Each independent action (create, join) is its own small section
// with its own submitting/error state, not one shared form.
export function LeaguesScreen({ accessToken, onAuthError }: LeaguesScreenProps) {
  const fetchFn = useCallback(() => fetchMyLeagues(accessToken), [accessToken]);
  const { data: leagues, hidden, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });

  if (loadError !== null || hidden) {
    return (
      <p className="leagues-screen__status leagues-screen__status--error">
        {loadError ?? 'Something went wrong loading your leagues.'}
      </p>
    );
  }

  if (leagues === null) {
    return <p className="leagues-screen__status">Loading…</p>;
  }

  return (
    <div className="leagues-screen">
      <h2 className="leagues-screen__title">Leagues</h2>

      <CreateLeagueSection accessToken={accessToken} onAuthError={onAuthError} onCreated={refetch} />
      <JoinLeagueSection accessToken={accessToken} onAuthError={onAuthError} onJoined={refetch} />
      <MyLeaguesSection leagues={leagues} />
    </div>
  );
}

interface CreateLeagueSectionProps {
  accessToken: string;
  onAuthError: () => void;
  onCreated: () => Promise<void>;
}

// REQ-402: a logged-in player creates a league with a name.
function CreateLeagueSection({ accessToken, onAuthError, onCreated }: CreateLeagueSectionProps) {
  const [name, setName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    // REQ-402: same "free checks before a database write" discipline as
    // AuthController.Signup's DisplayName check — matches
    // LeagueEndpoints.MaxNameLength server-side.
    const trimmed = name.trim();
    if (trimmed.length === 0 || trimmed.length > NAME_MAX_LENGTH) {
      setError(`League name must be between 1 and ${NAME_MAX_LENGTH} characters.`);
      return;
    }

    setSubmitting(true);
    try {
      await createLeague(accessToken, trimmed);
      setName('');
      await onCreated();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className="leagues-screen__section">
      <h3 className="leagues-screen__section-title">Create a league</h3>
      <form className="leagues-screen__form" onSubmit={handleSubmit}>
        <label className="leagues-screen__field">
          <span>League name</span>
          <input
            type="text"
            maxLength={NAME_MAX_LENGTH}
            value={name}
            onChange={(event) => setName(event.target.value)}
            disabled={submitting}
          />
        </label>

        {error && (
          <p className="leagues-screen__error" role="alert">
            {error}
          </p>
        )}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Creating…' : 'Create league'}
        </button>
      </form>
    </section>
  );
}

interface JoinLeagueSectionProps {
  accessToken: string;
  onAuthError: () => void;
  onJoined: () => Promise<void>;
}

// REQ-403: a player enters a valid invite_code to join a league — an
// invalid code shows the server's own clear error inline, same "server's
// own detail text shown inline" convention SettingsScreen's display-name
// conflict already uses, and never leaves the field looking like it
// succeeded.
function JoinLeagueSection({ accessToken, onAuthError, onJoined }: JoinLeagueSectionProps) {
  const [inviteCode, setInviteCode] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    const trimmed = inviteCode.trim();
    if (trimmed.length === 0) {
      setError('Invite code is required.');
      return;
    }

    setSubmitting(true);
    try {
      await joinLeague(accessToken, trimmed);
      setInviteCode('');
      await onJoined();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-403: an invalid code surfaces here via the server's own detail
      // text ("No league found with invite code '...'.") — never a generic
      // failure banner.
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className="leagues-screen__section">
      <h3 className="leagues-screen__section-title">Join a league</h3>
      <form className="leagues-screen__form" onSubmit={handleSubmit}>
        <label className="leagues-screen__field">
          <span>Invite code</span>
          <input
            type="text"
            value={inviteCode}
            onChange={(event) => setInviteCode(event.target.value)}
            disabled={submitting}
          />
        </label>

        {error && (
          <p className="leagues-screen__error" role="alert">
            {error}
          </p>
        )}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Joining…' : 'Join league'}
        </button>
      </form>
    </section>
  );
}

interface MyLeaguesSectionProps {
  leagues: CustomLeague[];
}

// This story's "simple list" of the player's own custom leagues — name and
// invite code only, no leaderboard rendering (REQ-404's separate,
// larger, tracked follow-up work).
function MyLeaguesSection({ leagues }: MyLeaguesSectionProps) {
  return (
    <section className="leagues-screen__section">
      <h3 className="leagues-screen__section-title">My leagues</h3>
      {leagues.length === 0 ? (
        // design-document.md §5: empty states are invitations.
        <p className="leagues-screen__empty">You're not in any custom leagues yet.</p>
      ) : (
        <ul className="leagues-screen__list">
          {leagues.map((league) => (
            <li key={league.id} className="leagues-screen__row">
              <span className="leagues-screen__row-name">{league.name}</span>
              <span className="leagues-screen__row-code">Code: {league.inviteCode}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
