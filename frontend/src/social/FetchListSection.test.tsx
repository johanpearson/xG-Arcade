import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { FetchListSection } from './FetchListSection';

// Quality-gate follow-up to commit 1203d47 (ADR-0084 code-health budget,
// rule-of-three): direct coverage of the shared loading/error/empty/list
// render shape extracted out of three near-identical copies in
// src/social/ (FriendsTab.tsx's two sections and ChallengesTab.tsx). Those
// components' own existing tests still exercise each of these four
// branches end-to-end — this file adds isolated coverage of the shared
// component itself, mirroring LeaderboardRowsList's own render-only test
// coverage in the leaderboard feature area.

describe('FetchListSection', () => {
  it('renders the error message and role="alert" when loadError is set, regardless of data', () => {
    render(
      <FetchListSection
        data={null}
        loadError="Something went wrong."
        emptyMessage="empty"
        renderList={() => <p>list</p>}
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong.');
    expect(screen.queryByText('empty')).not.toBeInTheDocument();
    expect(screen.queryByText('list')).not.toBeInTheDocument();
  });

  it('renders "Loading…" when data is null and there is no error', () => {
    render(<FetchListSection data={null} loadError={null} emptyMessage="empty" renderList={() => <p>list</p>} />);

    expect(screen.getByText('Loading…')).toBeInTheDocument();
  });

  it('renders emptyMessage when data resolved to an empty array', () => {
    render(<FetchListSection data={[]} loadError={null} emptyMessage="Nothing here." renderList={() => <p>list</p>} />);

    expect(screen.getByText('Nothing here.')).toBeInTheDocument();
    expect(screen.queryByText('list')).not.toBeInTheDocument();
  });

  it('renders renderList(data) when data has at least one item', () => {
    render(
      <FetchListSection
        data={['a', 'b']}
        loadError={null}
        emptyMessage="empty"
        renderList={(items) => <p>{items.join(',')}</p>}
      />,
    );

    expect(screen.getByText('a,b')).toBeInTheDocument();
  });
});
