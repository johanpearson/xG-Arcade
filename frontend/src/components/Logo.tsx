import './Logo.css';

// A minimal flat "ball" glyph — a green circle (safe on both themes, same
// reasoning as LogoMark's badge below) with one white pentagon, not a
// textured/shaded ball illustration. It's self-contained (carries its own
// circular backdrop) so it stays legible wherever it's placed, the same
// "self-contained badge, not page chrome" reasoning already applied to
// overlay-scrim's foreground pairings in design-document.md §2.
function BallAccent({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" className={className}>
      <circle cx="12" cy="12" r="11" fill="var(--color-accent-green)" />
      <polygon points="12,5 17.7,9.1 15.6,15.8 8.4,15.8 6.3,9.1" fill="#ffffff" />
    </svg>
  );
}

export interface LogoMarkProps {
  size?: number;
  className?: string;
}

// Simplified icon (favicon, app-icon-style use): an "xG" monogram on a
// rounded-square badge, plus the ball accent above tucked into the corner.
// Kept as a single fixed white-on-green treatment rather than the two-tone
// letters Logo (below) uses — accent-gold measures too low a contrast
// against accent-green's own background (both are similar-lightness
// saturated colors) to read reliably at icon sizes, so this stays the
// robust, self-contained version. Colors are fixed rather than
// theme-driven: --color-accent-green is already the same hex in both
// light and dark theme (design-document.md §2's dark-theme table), so this
// mark needs no dark-mode variant of its own. font-family is hardcoded
// (not var(--font-display)) so this component renders identically to the
// standalone favicon.svg, which has no access to the app's CSS custom
// properties.
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
        x="29"
        y="38"
        textAnchor="middle"
        dominantBaseline="central"
        fontFamily="'Space Grotesk', system-ui, sans-serif"
        fontWeight="700"
        fontSize="24"
        fill="#ffffff"
      >
        xG
      </text>
      <g transform="translate(45, 19)">
        <circle r="11" fill="#ffffff" />
        <polygon points="0,-6 5.2,-1.8 3,4.3 -3,4.3 -5.2,-1.8" fill="var(--color-accent-green)" />
      </g>
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
// data-theme override, same pattern SettingsScreen/CellState rely on) with
// the ball accent tucked against the G, plus "Arcade" as the one remaining
// word (not the full "xG Arcade" repeated next to its own monogram).
// "x"/"G" are two separate sibling elements (for independent coloring), and
// the accessible-name algorithm inserts a space between *each* child
// element's own contribution when accumulating a parent's name — not just
// between literal text runs — so left as plain siblings they'd compute as
// "x G Arcade", not "xG Arcade". The `aria-label="xG"` on their wrapping
// span makes that wrapper contribute a single atomic "xG" instead. Sizes
// via the inherited font-size (no separate icon-size prop, unlike the old
// badge-based version) — set font-size on whatever wraps it, same as any
// other text. Carries no heading/button semantics of its own so callers
// wrap it in whatever element their context needs (SplashScreen's <h1>,
// App.tsx's header title/button).
export function Logo({ className }: LogoProps) {
  return (
    <span className={['logo', className].filter(Boolean).join(' ')}>
      <span className="logo__xg" aria-label="xG">
        <span className="logo__x">x</span>
        <span className="logo__g-wrap">
          <span className="logo__g">G</span>
          <BallAccent className="logo__ball" />
        </span>
      </span>{' '}
      <span className="logo__arcade">Arcade</span>
    </span>
  );
}
