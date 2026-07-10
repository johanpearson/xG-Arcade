import { clubInitials, flagEmojiFor } from '../lib/categoryDisplay';
import './CategoryLabel.css';

export interface CategoryLabelProps {
  categoryType: string;
  value: string;
  size?: 'small' | 'medium';
}

// design-document.md §2/§6: a flag or badge is always paired with its text
// name — never the sole identifier. `categoryType` is a plain string at the
// type level (REQ-107: which axis is country vs. club varies per cell,
// never assumed statically) — but Tier 0 only ever has the two literal
// values "country"/"club" (CategoryPairingRules on the backend), so the
// branch below does compare against one of them directly.
export function CategoryLabel({ categoryType, value, size = 'medium' }: CategoryLabelProps) {
  return (
    <span className={`category-label category-label--${size}`}>
      {categoryType === 'country' ? (
        <FlagGlyph countryName={value} />
      ) : (
        <ClubBadge clubName={value} />
      )}
      <span className="category-label__name">{value}</span>
    </span>
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

function ClubBadge({ clubName }: { clubName: string }) {
  return (
    <span className="category-label__badge" aria-hidden="true">
      {clubInitials(clubName)}
    </span>
  );
}
