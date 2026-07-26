import './Logo.css';

export interface LogoMarkProps {
  size?: number;
  className?: string;
}

// Brand mark: an "xG" monogram on a rounded-square badge — xG is the actual
// term (expected goals) the whole product name is built on, so it's the
// mark's entire content, not a supporting detail beside a separate pictorial
// symbol. Colors are fixed rather than theme-driven: --color-accent-green is
// already the same hex in both light and dark theme (design-document.md §2's
// dark-theme table), and the monogram itself is a literal white for the same
// "self-contained badge, not page chrome" reasoning already applied to
// overlay-scrim's foreground pairings — so this mark needs no dark-mode
// variant. font-family is hardcoded (not var(--font-display)) so this
// component renders identically to the standalone favicon.svg, which has no
// access to the app's CSS custom properties.
export function LogoMark({ size = 40, className }: LogoMarkProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 64 64"
      role="img"
      aria-label="xG"
      className={className}
    >
      <rect width="64" height="64" rx="14" fill="var(--color-accent-green)" />
      <text
        x="32"
        y="35"
        textAnchor="middle"
        dominantBaseline="central"
        fontFamily="'Space Grotesk', system-ui, sans-serif"
        fontWeight="700"
        fontSize="27"
        fill="#ffffff"
      >
        xG
      </text>
    </svg>
  );
}

export interface LogoProps {
  className?: string;
  iconSize?: number;
}

// Lockup used in place of the plain "xG Arcade" text — the xG monogram
// badge above, plus "Arcade" as the one remaining word (not the full "xG
// Arcade" repeated next to its own monogram). The badge's own aria-label
// ("xG") plus this literal space plus "Arcade" gives the whole lockup the
// same accessible name ("xG Arcade") the plain-text heading had before, so
// SplashScreen's REQ-719 test is unaffected. Carries no heading semantics of
// its own so callers wrap it in whatever element their context needs
// (SplashScreen uses an <h1>).
export function Logo({ className, iconSize = 40 }: LogoProps) {
  return (
    <span className={['logo', className].filter(Boolean).join(' ')}>
      <LogoMark size={iconSize} /> <span className="logo__wordmark">Arcade</span>
    </span>
  );
}
