import { useEffect, useState } from 'react';
import { fetchUserAvatarImageObjectUrl } from '../lib/avatar';
import './PlayerAvatar.css';

// REQ-722/S-184: the other-players-facing avatar thumbnail
// requirements-document.md's REQ-722 status note (S-182) explicitly flagged
// as unbuilt — "no surface anywhere in the frontend renders another
// player's avatar yet." This component is that surface: a small, circular
// thumbnail usable for ANY userId (the viewer's own, or another player's —
// it has no own-vs-other concept, same pattern UserStatsScreen.tsx already
// established for the same reason). See design-document.md's SCREEN-08
// section (its now-corrected status note) for the visual spec this follows.
export interface PlayerAvatarProps {
  accessToken: string;
  userId: string;
  // Carried for context/testing only (e.g. asserting which player's avatar
  // a given instance is for) — never rendered as visible text by this
  // component itself. The surrounding screen's own display-name text is
  // what carries this player's accessible identity (§6); this component's
  // own image/placeholder is decorative only (`alt=""`/`aria-hidden`, see
  // below), same reasoning CellPlaceholderAvatar/CellPhoto in
  // frontend/src/grid/CellState.tsx already document for the same pairing
  // rule.
  displayName: string;
  // Pixel size of the rendered circle. Defaults to the 64×64px value
  // design-document.md's SCREEN-08 section already documents (its "New
  // layout value" note for the avatar-upload preview thumbnails) — reused
  // here rather than inventing a second dimension. If a call site needs a
  // different size, that new value must be documented in design-document.md
  // the same way (CLAUDE.md's "never introduce ... a layout value not
  // defined in design-document.md" rule applies to size the same way it
  // already applies to color/typeface/animation).
  size?: number;
}

const DEFAULT_SIZE_PX = 64;

// REQ-722/S-184: fetches GET /users/{userId}/avatar/image on mount and
// whenever accessToken/userId change, and manages its own object-URL
// lifecycle (revoke on unmount and on every URL change) — the same
// cancellation/revoke discipline SettingsScreen.tsx's own
// `useAvatarObjectUrl` hook already established for the self-view case
// (mirrored here, not imported — that hook is a local closure scoped to
// SettingsScreen's own `imageUrl`-relative-path shape, not this
// userId-based endpoint). On ANY failure (a 404 — no Approved avatar for
// that user — or any other error) this degrades quietly to the placeholder
// silhouette below: no visible error, same "failed preview fetch degrades
// to no preview" convention that hook's own `.catch()` already uses.
export function PlayerAvatar({ accessToken, userId, displayName, size = DEFAULT_SIZE_PX }: PlayerAvatarProps) {
  const [objectUrl, setObjectUrl] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let createdUrl: string | null = null;
    // Reset immediately on a userId/accessToken change, rather than keeping
    // a stale previous image showing while the new fetch is in flight —
    // this screen has no meaningful "loading" visual for this component
    // (a brief, sub-second gap, same as SettingsScreen.tsx's own avatar
    // section — see design-document.md's SCREEN-08 note on that), so the
    // placeholder is shown for that gap instead of a stale, wrong photo.
    setObjectUrl(null);

    fetchUserAvatarImageObjectUrl(accessToken, userId)
      .then((url) => {
        if (cancelled) {
          URL.revokeObjectURL(url);
          return;
        }
        createdUrl = url;
        setObjectUrl(url);
      })
      .catch(() => {
        if (!cancelled) setObjectUrl(null);
      });

    return () => {
      cancelled = true;
      if (createdUrl) URL.revokeObjectURL(createdUrl);
    };
  }, [accessToken, userId]);

  const dimension = `${size}px`;

  if (!objectUrl) {
    return (
      <div
        className="player-avatar player-avatar--placeholder"
        style={{ width: dimension, height: dimension }}
        data-testid="player-avatar-placeholder"
        data-player-avatar-user={displayName}
        aria-hidden="true"
      >
        {/* Same flat, single-tone person-silhouette shape as
            frontend/src/grid/CellState.tsx's CellPlaceholderAvatar
            (design-document.md §2's "Placeholder avatar (REQ-216)" entry) —
            recreated at thumbnail scale here rather than reusing that
            component directly, since its own doc comment scopes it to the
            full-bleed grid-cell treatment specifically. */}
        <svg className="player-avatar__placeholder-svg" viewBox="0 0 24 24" focusable="false">
          <circle cx="12" cy="8" r="4" fill="currentColor" />
          <path d="M4 21c0-4.42 3.58-8 8-8s8 3.58 8 8" fill="currentColor" />
        </svg>
      </div>
    );
  }

  return (
    <img
      className="player-avatar"
      style={{ width: dimension, height: dimension }}
      src={objectUrl}
      alt=""
      aria-hidden="true"
      data-testid="player-avatar-image"
      data-player-avatar-user={displayName}
    />
  );
}
