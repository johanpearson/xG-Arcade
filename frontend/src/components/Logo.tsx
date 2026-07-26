import './Logo.css';

export interface LogoMarkProps {
  size?: number;
  className?: string;
}

// Simplified icon (favicon, app-icon-style use): an "xG" monogram on a
// rounded-square badge. Kept as a single fixed white-on-green treatment
// rather than the two-tone letters Logo (below) uses — accent-gold
// measures too low a contrast against accent-green's own background (both
// are similar-lightness saturated colors) to read reliably at icon sizes,
// so this stays the robust, self-contained version. Colors are fixed
// rather than theme-driven: --color-accent-green is already the same hex
// in both light and dark theme (design-document.md §2's dark-theme
// table), so this mark needs no dark-mode variant of its own.
// font-family is hardcoded (not var(--font-display)) so this component
// renders identically to the standalone favicon.svg, which has no access
// to the app's CSS custom properties.
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
}

// Lockup used in place of the plain "xG Arcade" text — two-tone "xG" (green
// x, gold G, using --color-accent-gold-text rather than raw
// --color-accent-gold: this sits directly on the page's own bg-base, not on
// a self-contained badge like LogoMark, so it needs the WCAG-checked
// text/icon variant design-document.md §2 already defines — that token
// already resolves to the correct per-theme value via index.css's
// data-theme override, same pattern SettingsScreen/CellState rely on),
// plus "Arcade" as the one remaining word (not the full "xG Arcade"
// repeated next to its own monogram).
// "x"/"G" are two separate sibling elements (for independent coloring), and
// the accessible-name algorithm inserts a space between *each* child
// element's own contribution when accumulating a parent's name — not just
// between literal text runs — so left as plain siblings they'd compute as
// "x G Arcade", not "xG Arcade". The `aria-label="xG"` on their wrapping
// span makes that wrapper contribute a single atomic "xG" instead. Sizes
// via the inherited font-size — set font-size on whatever wraps it, same
// as any other text. Carries no heading/button semantics of its own so
// callers wrap it in whatever element their context needs (SplashScreen's
// <h1>, App.tsx's header title/button).
//
// **2026-07-26 (dropped the ball accent):** a flat ball glyph tucked
// against the G was tried the same day, per user-supplied inspiration —
// direct feedback afterward called it "too much" and not good-looking, so
// it was removed outright rather than kept as an option. Two-tone letters
// stayed; that part read well on its own.
export function Logo({ className }: LogoProps) {
  return (
    <span className={['logo', className].filter(Boolean).join(' ')}>
      <span className="logo__xg" aria-label="xG">
        <span className="logo__x">x</span>
        <span className="logo__g">G</span>
      </span>{' '}
      <span className="logo__arcade">Arcade</span>
    </span>
  );
}
