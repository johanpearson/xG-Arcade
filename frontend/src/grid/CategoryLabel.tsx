import { clubInitials, flagEmojiFor } from '../lib/categoryDisplay';
import './CategoryLabel.css';

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

function FlagGlyph({ countryName }: { countryName: string }) {
  const emoji = flagEmojiFor(countryName);
  if (!emoji) return null;
  return (
    <span className="category-label__flag" aria-hidden="true">
      {emoji}
    </span>
  );
}

function ClubBadge({ clubName, size = 'medium' }: { clubName: string; size?: 'small' | 'medium' }) {
  return (
    <span className={`category-label__badge category-label__badge--${size}`} aria-hidden="true">
      {clubInitials(clubName)}
    </span>
  );
}
