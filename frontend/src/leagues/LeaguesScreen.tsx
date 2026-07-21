import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, createLeague, describeError, fetchMyLeagues, joinLeague } from '../lib/api';
import type { CustomLeague } from '../lib/types';
import './LeaguesScreen.css';

export interface LeaguesScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

const NAME_MAX_LENGTH = 50;

type PageState =
  | { phase: 'loading' }
  | { phase: 'error'; message: string }
  | { phase: 'ready' };

// REQ-402/403: create a custom league and join one via invite code, plus a
// simple list of the caller's own custom leagues. Deliberately does NOT
// render a per-league leaderboard — that's REQ-404's separate, larger,
// tracked follow-up (a full per-custom-league leaderboard with tab
// switching); this screen's whole scope is create/join/list, matching the
// "simple list is enough" story boundary. Structured the same way
// SettingsScreen.tsx already establishes for this codebase's screens: a
// `PageState` union gates loading/error/ready, and each independent action
// (create, join) is its own small section with its own submitting/error
// state, not one shared form.
export function LeaguesScreen({ accessToken, onAuthError }: LeaguesScreenProps) {
  const [pageState, setPageState] = useState<PageState>({ phase: 'loading' });
  const [leagues, setLeagues] = useState<CustomLeague[]>([]);

  const refreshLeagues = useCallback(async () => {
    const mine = await fetchMyLeagues(accessToken);
    setLeagues(mine);
  }, [accessToken]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const mine = await fetchMyLeagues(accessToken);
        if (cancelled) return;
        setLeagues(mine);
        setPageState({ phase: 'ready' });
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
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

  if (pageState.phase === 'loading') {
    return <p className="leagues-screen__status">Loading…</p>;
  }

  if (pageState.phase === 'error') {
    return <p className="leagues-screen__status leagues-screen__status--error">{pageState.message}</p>;
  }

  return (
    <div className="leagues-screen">
      <h2 className="leagues-screen__title">Leagues</h2>

      <CreateLeagueSection accessToken={accessToken} onAuthError={onAuthError} onCreated={refreshLeagues} />
      <JoinLeagueSection accessToken={accessToken} onAuthError={onAuthError} onJoined={refreshLeagues} />
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
