// REQ-722/S-184 (2026-08-25 quality-architect fix pass): the flat,
// single-tone person-silhouette glyph (circle head + rounded-shoulder body)
// used as a "no avatar" placeholder in three places —
// frontend/src/grid/CellState.tsx's CellPlaceholderAvatar (REQ-216, the
// original), frontend/src/components/PlayerAvatar.tsx, and
// frontend/src/settings/SettingsScreen.tsx's ProfileAvatarPreview (both
// REQ-722/S-184) — extracted here once the third copy landed, per
// docs/coding-guidelines.md's Code health budget rule-of-three.
//
// Deliberately just the `<svg>` body, nothing else: every call site keeps
// its own wrapper `div`/className/sizing/`aria-hidden` treatment exactly as
// it already had it (this component only removes the copy-pasted markup,
// not any of the three sites' own layout or accessibility decisions).
// `className` is passed straight through to the `<svg>` element so each
// caller can still scope its own CSS to the icon itself (e.g.
// `.player-avatar__placeholder-svg`, `.cell-state__placeholder-avatar`) —
// this component has no opinion on color/size, both of which stay
// controlled by the caller's own CSS (`color`/`width`/`height`) exactly as
// before extraction.
export interface PersonSilhouetteIconProps {
  className?: string;
}

export function PersonSilhouetteIcon({ className }: PersonSilhouetteIconProps) {
  return (
    <svg className={className} viewBox="0 0 24 24" focusable="false">
      <circle cx="12" cy="8" r="4" fill="currentColor" />
      <path d="M4 21c0-4.42 3.58-8 8-8s8 3.58 8 8" fill="currentColor" />
    </svg>
  );
}
