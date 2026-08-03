/* oxlint-disable react/only-export-components -- this file also exports the
   plain hasCountryFlag(name) boolean helper alongside the CountryFlag
   component; splitting it into its own file just to satisfy the fast-
   refresh lint rule would be worse for readability than the warning it
   silences. */
import type { ReactElement } from 'react';

// Bug fix (2026-08-03, user-tester report): flags used to be rendered as
// Unicode regional-indicator flag emoji (design-document.md §2's original
// "flags render as standard flag emoji/Unicode — safe, no licensing
// concern"). That degrades badly on Windows: Chrome (and Edge) render emoji
// through the host OS font, and Windows dropped color flag glyphs from its
// system font years ago, so a flag emoji falls back to its two literal
// Regional Indicator Symbol letters (e.g. "GB") with no flag graphic at all
// — exactly what the tester saw ("no flags"), visible in their own
// screenshot as bare "ES"/"GB"/"SN" text next to each country name. Firefox
// only avoids this because it bundles its own color emoji font (Twemoji
// Mozilla) and never asks the OS to render the glyph.
//
// Fixed by rendering flags as small inline SVGs bundled directly in the JS
// bundle instead — rendering no longer depends on the host OS/browser's
// font at all, so it's consistent across every platform. Same self-contained
// spirit as this codebase's club badge (CategoryLabel.tsx's ClubBadge):
// simplified geometric shapes, not licensed crest/flag artwork, so there's
// nothing here beyond a country's real, public national colors — same "no
// licensing concern" design-document.md §2 already established for flags
// generally. Deliberately simplified (solid bands/crosses/a plain circle
// rather than exact coats of arms, stars, or sunbursts) — same economy
// ClubBadge already applies by using initials instead of a real crest — the
// text name next to every flag (§6 "never the sole identifier") covers the
// rest.
//
// Covers the same Tier 0 country list categoryDisplay.ts's old
// FLAG_EMOJI_BY_COUNTRY table did — a country not listed here simply renders
// without a flag glyph, never blocking rendering (same contract as before).
const FLAG_WIDTH = 30;
const FLAG_HEIGHT = 20;

interface Band {
  color: string;
  weight?: number;
}

function horizontalBands(bands: Band[]) {
  const total = bands.reduce((sum, b) => sum + (b.weight ?? 1), 0);
  let y = 0;
  return bands.map((band, index) => {
    const height = ((band.weight ?? 1) / total) * FLAG_HEIGHT;
    const rect = <rect key={index} x={0} y={y} width={FLAG_WIDTH} height={height} fill={band.color} />;
    y += height;
    return rect;
  });
}

function verticalBands(bands: Band[]) {
  const total = bands.reduce((sum, b) => sum + (b.weight ?? 1), 0);
  let x = 0;
  return bands.map((band, index) => {
    const width = ((band.weight ?? 1) / total) * FLAG_WIDTH;
    const rect = <rect key={index} x={x} y={0} width={width} height={FLAG_HEIGHT} fill={band.color} />;
    x += width;
    return rect;
  });
}

// Scandinavian cross: an off-center vertical bar (shifted toward the hoist
// side, per every real Nordic flag) plus a full-width horizontal bar.
function scandinavianCross(background: string, crossColor: string) {
  return (
    <>
      <rect x={0} y={0} width={FLAG_WIDTH} height={FLAG_HEIGHT} fill={background} />
      <rect x={9} y={0} width={4} height={FLAG_HEIGHT} fill={crossColor} />
      <rect x={0} y={8} width={FLAG_WIDTH} height={4} fill={crossColor} />
    </>
  );
}

function starPoints(cx: number, cy: number, outerR: number, innerR: number): string {
  const points: string[] = [];
  for (let i = 0; i < 10; i++) {
    const r = i % 2 === 0 ? outerR : innerR;
    const angle = (Math.PI / 5) * i - Math.PI / 2;
    points.push(`${cx + r * Math.cos(angle)},${cy + r * Math.sin(angle)}`);
  }
  return points.join(' ');
}

// Union Jack, simplified: navy field, white diagonals (St Andrew's cross),
// thinner red diagonals on top, then the white/red St George's cross —
// geometrically approximate (a real Union Jack's diagonals are counter-
// changed per quadrant), but this reads unmistakably as the UK flag at the
// small size it's ever shown at here.
function unitedKingdomFlag() {
  return (
    <>
      <rect x={0} y={0} width={FLAG_WIDTH} height={FLAG_HEIGHT} fill="#012169" />
      <line x1={0} y1={0} x2={FLAG_WIDTH} y2={FLAG_HEIGHT} stroke="#FFFFFF" strokeWidth={4} />
      <line x1={FLAG_WIDTH} y1={0} x2={0} y2={FLAG_HEIGHT} stroke="#FFFFFF" strokeWidth={4} />
      <line x1={0} y1={0} x2={FLAG_WIDTH} y2={FLAG_HEIGHT} stroke="#C8102E" strokeWidth={1.6} />
      <line x1={FLAG_WIDTH} y1={0} x2={0} y2={FLAG_HEIGHT} stroke="#C8102E" strokeWidth={1.6} />
      <rect x={0} y={8} width={FLAG_WIDTH} height={4} fill="#FFFFFF" />
      <rect x={13} y={0} width={4} height={FLAG_HEIGHT} fill="#FFFFFF" />
      <rect x={0} y={9} width={FLAG_WIDTH} height={2} fill="#C8102E" />
      <rect x={14} y={0} width={2} height={FLAG_HEIGHT} fill="#C8102E" />
    </>
  );
}

function brazilFlag() {
  return (
    <>
      <rect x={0} y={0} width={FLAG_WIDTH} height={FLAG_HEIGHT} fill="#009739" />
      <polygon points="15,2 28,10 15,18 2,10" fill="#FEDD00" />
      <circle cx={15} cy={10} r={5} fill="#012169" />
    </>
  );
}

function argentinaFlag() {
  return (
    <>
      {horizontalBands([{ color: '#75AADB' }, { color: '#FFFFFF' }, { color: '#75AADB' }])}
      <circle cx={15} cy={10} r={2.4} fill="#F6B40E" stroke="#85340A" strokeWidth={0.4} />
    </>
  );
}

function uruguayFlag() {
  const stripeColors = ['#FFFFFF', '#0038A8', '#FFFFFF', '#0038A8', '#FFFFFF'];
  return (
    <>
      {horizontalBands(stripeColors.map((color) => ({ color })))}
      <rect x={0} y={0} width={12} height={8} fill="#FFFFFF" />
      <circle cx={6} cy={4} r={2.6} fill="#FCD116" />
    </>
  );
}

function senegalFlag() {
  return (
    <>
      {verticalBands([{ color: '#00853F' }, { color: '#FDEF42' }, { color: '#E31B23' }])}
      <polygon points={starPoints(15, 10, 2.4, 1)} fill="#00853F" />
    </>
  );
}

// Simplified — real Portuguese/Croatian/Serbian flags carry a coat of arms
// this deliberately omits, same "no real crest artwork" economy as the club
// badge (see this file's own top-of-file comment).
const FLAG_RENDERERS: Record<string, () => ReactElement> = {
  Brazil: brazilFlag,
  Argentina: argentinaFlag,
  France: () => <>{verticalBands([{ color: '#0055A4' }, { color: '#FFFFFF' }, { color: '#EF4135' }])}</>,
  Germany: () => <>{horizontalBands([{ color: '#000000' }, { color: '#DD0000' }, { color: '#FFCE00' }])}</>,
  Spain: () => <>{horizontalBands([{ color: '#AA151B', weight: 1 }, { color: '#F1BF00', weight: 2 }, { color: '#AA151B', weight: 1 }])}</>,
  'United Kingdom': unitedKingdomFlag,
  Italy: () => <>{verticalBands([{ color: '#009246' }, { color: '#FFFFFF' }, { color: '#CE2B37' }])}</>,
  Netherlands: () => <>{horizontalBands([{ color: '#AE1C28' }, { color: '#FFFFFF' }, { color: '#21468B' }])}</>,
  Portugal: () => <>{verticalBands([{ color: '#046A38', weight: 2 }, { color: '#DA291C', weight: 3 }])}</>,
  Belgium: () => <>{verticalBands([{ color: '#000000' }, { color: '#FFD90C' }, { color: '#ED2939' }])}</>,
  Croatia: () => <>{horizontalBands([{ color: '#FF0000' }, { color: '#FFFFFF' }, { color: '#171796' }])}</>,
  Uruguay: uruguayFlag,
  Colombia: () => <>{horizontalBands([{ color: '#FCD116', weight: 2 }, { color: '#003893', weight: 1 }, { color: '#CE1126', weight: 1 }])}</>,
  Nigeria: () => <>{verticalBands([{ color: '#008751' }, { color: '#FFFFFF' }, { color: '#008751' }])}</>,
  Senegal: senegalFlag,
  'Ivory Coast': () => <>{verticalBands([{ color: '#FF8200' }, { color: '#FFFFFF' }, { color: '#009A44' }])}</>,
  Serbia: () => <>{horizontalBands([{ color: '#C6363C' }, { color: '#0C4076' }, { color: '#FFFFFF' }])}</>,
  Poland: () => <>{horizontalBands([{ color: '#FFFFFF' }, { color: '#DC143C' }])}</>,
  Sweden: () => scandinavianCross('#006AA7', '#FECC00'),
  Denmark: () => scandinavianCross('#C60C30', '#FFFFFF'),
};

export function hasCountryFlag(countryName: string): boolean {
  return countryName in FLAG_RENDERERS;
}

export function CountryFlag({ countryName }: { countryName: string }) {
  const renderContent = FLAG_RENDERERS[countryName];
  if (!renderContent) return null;

  return (
    <svg
      className="category-label__flag"
      viewBox={`0 0 ${FLAG_WIDTH} ${FLAG_HEIGHT}`}
      role="img"
      aria-hidden="true"
      focusable="false"
    >
      {renderContent()}
    </svg>
  );
}
