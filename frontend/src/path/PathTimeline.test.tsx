import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PathTimeline } from './PathTimeline';
import type { PathClueTurn } from '../lib/types';

// Same minimal fake MediaQueryList pattern lib/theme.test.ts already
// establishes for this project's jsdom (which doesn't implement
// window.matchMedia at all) — see that file's own doc comment.
function stubMatchMedia(matches: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn().mockImplementation(() => ({
      matches,
      media: '(prefers-reduced-motion: reduce)',
      addEventListener: () => {},
      removeEventListener: () => {},
    })),
  );
}

const clubTurn = (turnNumber: number, clubs: { clubName: string; appearanceCount: number | null }[]): PathClueTurn => ({
  turnNumber,
  kind: 'ClubReveal',
  clubs,
  yearRanges: null,
  textValue: null,
});

const yearRangeTurn = (turnNumber: number, yearRanges: string[]): PathClueTurn => ({
  turnNumber,
  kind: 'YearRange',
  clubs: null,
  yearRanges,
  textValue: null,
});

const textTurn = (turnNumber: number, kind: 'Position' | 'Nationality' | 'Age', textValue: string): PathClueTurn => ({
  turnNumber,
  kind,
  clubs: null,
  yearRanges: null,
  textValue,
});

describe('PathTimeline', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1203: renders club-reveal turns, then the bundled year-range turn, then position/nationality/age in that fixed order', () => {
    const clues: PathClueTurn[] = [
      clubTurn(1, [{ clubName: 'Ajax', appearanceCount: 74 }]),
      clubTurn(2, [{ clubName: 'Juventus', appearanceCount: 94 }]),
      clubTurn(3, [{ clubName: 'Inter Milan', appearanceCount: 88 }]),
      yearRangeTurn(4, ['2001-04', '2004-06', '2006-09']),
      textTurn(5, 'Position', 'Forward'),
      textTurn(6, 'Nationality', 'Netherlands'),
      textTurn(7, 'Age', '1980'),
    ];

    render(<PathTimeline clues={clues} solved={false} />);

    const nodes = screen.getAllByRole('listitem');
    expect(nodes).toHaveLength(7);
    expect(nodes[0]).toHaveTextContent('Ajax');
    expect(nodes[0]).toHaveTextContent('74 apps');
    expect(nodes[1]).toHaveTextContent('Juventus');
    expect(nodes[2]).toHaveTextContent('Inter Milan');
    // REQ-1203: the year-range turn pairs each range back up with the club
    // it belongs to, in the same order the clubs were revealed.
    expect(nodes[3]).toHaveTextContent('Ajax 2001-04');
    expect(nodes[3]).toHaveTextContent('Juventus 2004-06');
    expect(nodes[3]).toHaveTextContent('Inter Milan 2006-09');
    expect(nodes[4]).toHaveTextContent('Position:');
    expect(nodes[4]).toHaveTextContent('Forward');
    expect(nodes[5]).toHaveTextContent('Nationality:');
    expect(nodes[5]).toHaveTextContent('Netherlands');
    expect(nodes[6]).toHaveTextContent('Age:');
    expect(nodes[6]).toHaveTextContent('1980');
  });

  it('REQ-1203: a club with an unknown appearance count is still revealed, with no "0 apps"/placeholder text', () => {
    const clues: PathClueTurn[] = [clubTurn(1, [{ clubName: 'Ajax', appearanceCount: null }])];

    render(<PathTimeline clues={clues} solved={false} />);

    expect(screen.getByText('Ajax')).toBeInTheDocument();
    expect(screen.queryByText(/apps/)).not.toBeInTheDocument();
    expect(screen.queryByText('0 apps')).not.toBeInTheDocument();
  });

  it('REQ-1207: a "not available" position/nationality/age still renders its own turn rather than being skipped', () => {
    const clues: PathClueTurn[] = [textTurn(1, 'Nationality', 'not available')];

    render(<PathTimeline clues={clues} solved={false} />);

    expect(screen.getByText('not available')).toBeInTheDocument();
  });

  it('REQ-1204: a correct guess renders the final node as solved (gold), showing the resolved player\'s name', () => {
    const clues: PathClueTurn[] = [
      clubTurn(1, [{ clubName: 'Ajax', appearanceCount: 74 }]),
      clubTurn(2, [{ clubName: 'Juventus', appearanceCount: 94 }]),
    ];

    render(
      <PathTimeline
        clues={clues}
        solved
        resolvedPlayerName="Zlatan Ibrahimović"
        resolvedPlayerPhotoUrl={null}
      />,
    );

    const nodes = screen.getAllByRole('listitem');
    expect(nodes).toHaveLength(2);
    // §6: never a color-only signal — "Solved" is real text, not just a
    // gold class name.
    expect(nodes[1]).toHaveTextContent('Solved');
    expect(nodes[1]).toHaveTextContent('Zlatan Ibrahimović');
    expect(nodes[1].className).toContain('path-timeline__node--solved');
    // The earlier, un-solved node is unaffected.
    expect(nodes[0].className).not.toContain('path-timeline__node--solved');
  });

  it('REQ-1203: a correct guess at any point means no further turn is ever rendered beyond what was actually revealed', () => {
    // Only 2 turns were ever revealed before the puzzle was solved on the
    // 2nd attempt — GET /path/current's own contract (only unlocked turns
    // are sent at all), so this component has nothing beyond `clues` to
    // render regardless of the puzzle's fixed 7-turn maximum.
    const clues: PathClueTurn[] = [
      clubTurn(1, [{ clubName: 'Ajax', appearanceCount: 74 }]),
      clubTurn(2, [{ clubName: 'Juventus', appearanceCount: 94 }]),
    ];

    render(<PathTimeline clues={clues} solved resolvedPlayerName="Zlatan Ibrahimović" resolvedPlayerPhotoUrl={null} />);

    expect(screen.getAllByRole('listitem')).toHaveLength(2);
  });

  it('prefers-reduced-motion: disables the settle-in animation class entirely (no partial-motion state)', () => {
    stubMatchMedia(true);
    const clues: PathClueTurn[] = [clubTurn(1, [{ clubName: 'Ajax', appearanceCount: 74 }])];

    render(<PathTimeline clues={clues} solved={false} />);

    const node = screen.getByRole('listitem');
    expect(node.className).not.toContain('path-timeline__node--animate-in');
  });

  it('without a reduced-motion preference, the settle-in animation class is applied', () => {
    stubMatchMedia(false);
    const clues: PathClueTurn[] = [clubTurn(1, [{ clubName: 'Ajax', appearanceCount: 74 }])];

    render(<PathTimeline clues={clues} solved={false} />);

    const node = screen.getByRole('listitem');
    expect(node.className).toContain('path-timeline__node--animate-in');
  });
});
