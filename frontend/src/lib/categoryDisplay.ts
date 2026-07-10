// design-document.md §2/§3: flags render as Unicode emoji (safe, no
// licensing concern); club crests are deferred to Phase 2, so v1 uses a
// circular initial-badge as the real v1 design, not a stub.
//
// Covers MVP-SCOPE.md's Tier 0 country list (~20 countries). A country not
// in this table simply renders without a flag glyph — never blocks
// rendering, and the visible text name is always shown regardless (§6:
// flag/badge is never the sole identifier).
const FLAG_EMOJI_BY_COUNTRY: Record<string, string> = {
  Brazil: '🇧🇷',
  Argentina: '🇦🇷',
  France: '🇫🇷',
  Germany: '🇩🇪',
  Spain: '🇪🇸',
  'United Kingdom': '🇬🇧',
  Italy: '🇮🇹',
  Netherlands: '🇳🇱',
  Portugal: '🇵🇹',
  Belgium: '🇧🇪',
  Croatia: '🇭🇷',
  Uruguay: '🇺🇾',
  Colombia: '🇨🇴',
  Nigeria: '🇳🇬',
  Senegal: '🇸🇳',
  'Ivory Coast': '🇨🇮',
  Serbia: '🇷🇸',
  Poland: '🇵🇱',
  Sweden: '🇸🇪',
  Denmark: '🇩🇰',
};

export function flagEmojiFor(countryName: string): string | null {
  return FLAG_EMOJI_BY_COUNTRY[countryName] ?? null;
}

// First 1-2 letters of the club name, used inside the circular placeholder
// badge (design-document.md §1's "Imagery note" — the real v1 design, not a
// temporary stand-in). Multi-word names use one initial per word (up to 2);
// single-word names use its first two letters.
export function clubInitials(clubName: string): string {
  const words = clubName.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return '';
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[1][0]).toUpperCase();
}
