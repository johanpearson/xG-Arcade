import './Logo.css';

export interface LogoMarkProps {
  size?: number;
  className?: string;
}

// Brand mark: a 2x2 grid with one gold cell — the xG Grid game's own
// "correct match" moment, in miniature (design-document.md §2: gold means
// settled/correct). The green badge and gold cell reuse
// --color-accent-green/--color-accent-gold, which that same document's
// dark-theme table keeps at identical hex values in both themes, so this
// mark never needs a dark-mode variant of its own. The three neutral cells
// are a literal white rather than --color-surface-card (which does flip
// dark in dark theme) for the same reason overlay-scrim's foreground
// pairings stay fixed regardless of theme (§2): this is a self-contained
// badge, not page chrome sitting on bg-base.
export function LogoMark({ size = 40, className }: LogoMarkProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 64 64"
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      <rect width="64" height="64" rx="14" fill="var(--color-accent-green)" />
      <rect x="7" y="7" width="22" height="22" rx="5" fill="#ffffff" />
      <rect x="35" y="7" width="22" height="22" rx="5" fill="#ffffff" />
      <rect x="7" y="35" width="22" height="22" rx="5" fill="#ffffff" />
      <rect x="35" y="35" width="22" height="22" rx="5" fill="var(--color-accent-gold)" />
    </svg>
  );
}

export interface LogoProps {
  className?: string;
  iconSize?: number;
}

// Lockup used in place of the plain "xG Arcade" text — icon mark plus
// wordmark. Carries no heading semantics of its own so callers wrap it in
// whatever element their context needs (SplashScreen uses an <h1>).
export function Logo({ className, iconSize = 40 }: LogoProps) {
  return (
    <span className={['logo', className].filter(Boolean).join(' ')}>
      <LogoMark size={iconSize} />
      <span className="logo__wordmark">xG Arcade</span>
    </span>
  );
}
