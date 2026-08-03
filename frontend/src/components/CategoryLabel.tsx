import { clubInitials } from '../lib/categoryDisplay';
import { CountryFlag } from '../lib/countryFlags';
import './CategoryLabel.css';

// Quality-gate fix (S-086 follow-up): moved here from frontend/src/grid/
// (xG Grid's own module directory) — PathTimeline.tsx (frontend/src/path/,
// xG Path's module directory) needed CategoryGlyph too (REQ-1203's club
// clues), and reaching into a peer game module's own directory for it was a
// cross-game-module import this repo's own module layout doesn't allow.
// frontend/src/components/ is this codebase's existing shared-component
// location (see Logo.tsx here already) — this is a genuinely
// cross-cutting, game-agnostic flag/badge renderer (REQ-107's country/club
// pairing isn't specific to either game), not something owned by xG Grid
// that xG Path happens to borrow.

export interface CategoryLabelProps {
  categoryType: string;
  value: string;
  size?: 'small' | 'medium';
}

export type CategoryGlyphProps = CategoryLabelProps;

// design-document.md §2/§6: a flag or badge is always paired with its text
// name — never the sole identifier. `categoryType` is a plain string at the
// type level (REQ-107: which axis is country vs. club varies per cell,
// never assumed statically) — but Tier 0 only ever has the two literal
// values "country"/"club" (CategoryPairingRules on the backend), so the
// branch below does compare against one of them directly.
export function CategoryLabel({ categoryType, value, size = 'medium' }: CategoryLabelProps) {
  return (
    <span className={`category-label category-label--${size}`}>
      <CategoryGlyph categoryType={categoryType} value={value} size={size} />
      <span className="category-label__name">{value}</span>
    </span>
  );
}

// Just the flag/badge glyph, no paired text label — extracted so
// CellState's badge-dock animation (S-015, design-document.md §2's
// "signature element") can reuse the same flag/badge rendering inside a
// cell, where the full text label already exists in the row/column header
// outside the cell (§6's "never the sole identifier" is satisfied there).
export function CategoryGlyph({ categoryType, value, size = 'medium' }: CategoryGlyphProps) {
  return categoryType === 'country' ? (
    // Flags don't take `size`: the emoji scales via ambient `1.1em` off
    // whatever font-size its container sets (see CategoryLabel.css), unlike
    // the club badge's fixed pixel circle, which needs a discrete variant.
    <FlagGlyph countryName={value} />
  ) : (
    <ClubBadge clubName={value} size={size} />
  );
}

// Bug fix (2026-08-03, user-tester report): was a Unicode flag emoji span —
// see countryFlags.tsx's own top-of-file comment for why that broke on
// Windows Chrome/Edge (no flag glyph in the host font, degrading to bare
// "GB"-style regional-indicator letters) and why this renders a bundled SVG
// instead, which needs no host font support at all.
function FlagGlyph({ countryName }: { countryName: string }) {
  return <CountryFlag countryName={countryName} />;
}

function ClubBadge({ clubName, size = 'medium' }: { clubName: string; size?: 'small' | 'medium' }) {
  return (
    <span className={`category-label__badge category-label__badge--${size}`} aria-hidden="true">
      {clubInitials(clubName)}
    </span>
  );
}
