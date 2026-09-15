import { expect, test, type APIRequestContext } from '@playwright/test'
import {
  loginForApi,
  seedConnectPlayers,
  signUpNewConnectPlayer,
  submitClosingChainStep,
  submitTargetPick,
} from './connect-playthrough'

// S-246 (docs/backlog.md): play-connect.spec.ts already covers playing a
// single xG Connect match end-to-end (via an API-seeded friendship), and
// leaderboard-leagues.spec.ts already covers REQ-402/403's custom-league
// invite flow — but nothing exercises the actual matchmaking MECHANISMS
// that get two players into a match in the first place: REQ-1401's friend
// request send/accept, REQ-1402's direct challenge-a-friend, and REQ-1403's
// opt-in random matchmaking (the opt-in call itself, not its 12h pairing
// window — see the Matchmaking section below for why). REQ-1417's
// Not-Started/Ongoing/Completed match-list tabs already have dedicated
// coverage in play-connect.spec.ts, so this file doesn't re-prove them
// beyond what naturally falls out of the flow below. REQ-1418 (opponent's
// chain visible after resolution) is also already asserted once in
// play-connect.spec.ts, but against an API-seeded friendship — this file's
// own job is proving it holds up in a genuine A-sends-request/B-accepts/
// A-challenges/B-accepts flow too, driven through the real UI rather than
// seeded.
//
// REQ-1401 has no user-search-by-name endpoint (deliberately — see
// SendFriendRequestAction.tsx's own top-of-file comment, ADR-0007's
// PlayerNameIndex/PlayerData boundary is a different concept entirely) —
// the only UI entry point into sending a friend request is
// SendFriendRequestAction on UserStatsScreen, reached by clicking a
// display name on a leaderboard row. Getting User B onto a leaderboard row
// User A can click therefore needs the same seed-a-round/force-close/
// browse-Previous-Rounds detour leaderboard-leagues.spec.ts's own REQ-408
// test already established — mirrored here rather than reinvented.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

interface SeedGuessableRoundResponse {
  roundId: string
  cellId: string
  correctPlayerName: string
}

interface ClosedRoundSummaryApi {
  roundId: string
  closedAt: string
}

interface ClosedRoundListResponseApi {
  rounds: ClosedRoundSummaryApi[]
}

// This spec does more round trips than play-connect.spec.ts (two full
// signups, a leaderboard detour, two friend-request/challenge round trips,
// then a full match to resolution) — sized generously rather than tuned
// tight, same "costs nothing" reasoning play-connect.spec.ts's own timeout
// comment gives.
test.describe.configure({ mode: 'serial', timeout: 150_000 })

test.describe('REQ-1401/1402/1403/1417/1418: friend request, challenge, matchmaking opt-in, and opponent-chain reveal', () => {
  // Same shape as leaderboard-leagues.spec.ts's own fetchClosedAtForRound —
  // lets the UI navigation step below select the exact seeded round by its
  // real `closedAt` text rather than relying on list position, which isn't
  // safe under `fullyParallel: true` with other spec files closing their
  // own xG Grid rounds concurrently (see that file's own shared-Active-
  // round comment for the full reasoning).
  async function fetchClosedAtForRound(
    request: APIRequestContext,
    accessToken: string,
    roundId: string,
  ): Promise<string> {
    const response = await request.get(`${API_BASE_URL}/leagues/global/leaderboard/closed-rounds`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    })
    expect(response.ok(), `closed-rounds list failed: ${response.status()}`).toBeTruthy()
    const body = (await response.json()) as ClosedRoundListResponseApi
    const match = body.rounds.find((round) => round.roundId === roundId)
    if (!match) {
      throw new Error(
        `Seeded round ${roundId} wasn't found in the first page of /leagues/global/leaderboard/closed-rounds ` +
          `(${body.rounds.length} rounds shown) — likely pushed off page 1 by concurrent spec files closing ` +
          'their own xG Grid rounds; see leaderboard-leagues.spec.ts\'s shared-Active-round comment.',
      )
    }
    return match.closedAt
  }

  test('REQ-1401/1402/1403/1417/1418: A sends a friend request, B accepts, A challenges, B accepts, both opt into matchmaking, and the match resolves with each side seeing the other\'s chain', async ({
    browser,
    request,
  }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const emailA = `test-friends-a-${tag}@test.invalid`
    const emailB = `test-friends-b-${tag}@test.invalid`
    const nameA = `Friends Player A ${tag}`
    const nameB = `Friends Player B ${tag}`

    // ---- Setup: get User B onto a leaderboard row User A can click --------
    const seedRound = await request.post(`${API_BASE_URL}/internal/test-data/seed-guessable-round`)
    expect(seedRound.ok(), `seed-guessable-round failed: ${seedRound.status()}`).toBeTruthy()
    const { roundId, cellId, correctPlayerName } = (await seedRound.json()) as SeedGuessableRoundResponse

    const contextA = await browser.newContext()
    const contextB = await browser.newContext()
    try {
      const pageA = await contextA.newPage()
      const pageB = await contextB.newPage()

      await signUpNewConnectPlayer(pageA, nameA, emailA)
      await signUpNewConnectPlayer(pageB, nameB, emailB)

      // User B submits the correct guess against the seeded round, then the
      // round is force-closed — both via the API, same as
      // leaderboard-leagues.spec.ts's own REQ-408 test, since this spec's
      // subject is the friending/challenging/matchmaking flow, not xG
      // Grid's guess-submission mechanics.
      const accessTokenB = await loginForApi(request, emailB)
      const guessResponse = await request.post(`${API_BASE_URL}/rounds/${roundId}/cells/${cellId}/guesses`, {
        headers: { Authorization: `Bearer ${accessTokenB}` },
        data: { submittedName: correctPlayerName },
      })
      expect(guessResponse.ok(), `submit guess failed: ${guessResponse.status()}`).toBeTruthy()
      const guess = (await guessResponse.json()) as { isCorrect: boolean }
      expect(guess.isCorrect).toBe(true)

      const closeResponse = await request.post(`${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`)
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()

      const accessTokenA = await loginForApi(request, emailA)
      const closedAt = await fetchClosedAtForRound(request, accessTokenA, roundId)

      // ---- User A navigates the leaderboard to find User B's row ----------
      // signUpNewConnectPlayer already landed pageA on GameSelectScreen with
      // a real, logged-in session (HeaderNav renders as soon as an access
      // token is set, regardless of which screen is showing) — no separate
      // login step needed.
      await pageA.getByRole('button', { name: 'Leaderboard' }).click()
      await pageA.getByRole('tab', { name: 'Previous Rounds' }).click()
      const roundButton = pageA.getByRole('button', { name: closedAt })
      await expect(roundButton).toBeVisible({ timeout: 10_000 })
      await roundButton.click()

      const rowB = pageA.getByRole('listitem').filter({ hasText: nameB })
      await expect(rowB).toBeVisible()
      await rowB.getByRole('button', { name: nameB }).click()

      // ---- REQ-1401: friend request send (User A) / accept (User B) -------
      // Lands on UserStatsScreen for User B (LeaderboardRowsList's own
      // onSelectPlayer navigation) — SendFriendRequestAction mounts here
      // since viewerUserId (A) differs from the viewed userId (B).
      await pageA.getByRole('button', { name: 'Send friend request' }).click()
      await expect(pageA.getByText('Friend request sent.')).toBeVisible()

      // FriendsScreen.tsx defaults to its "Friends" tab on mount — the
      // pending request row shows the requester's (User A's) display name,
      // per FriendsTab.tsx's PendingFriendRequestRow.
      await pageB.getByRole('button', { name: /^Friends/ }).click()
      const pendingRequestRow = pageB.getByRole('listitem').filter({ hasText: nameA })
      await expect(pendingRequestRow).toBeVisible()
      await pendingRequestRow.getByRole('button', { name: 'Accept' }).click()

      // FriendsTab.tsx's handleRequestResolved refetches both the pending
      // list AND the friends list on accept — the accepted row disappears
      // from "Friend requests" (FriendsTab.tsx has no separate post-accept
      // banner/copy of its own) and User A now appears under "My friends"
      // instead.
      await expect(pendingRequestRow).not.toBeVisible()
      const friendRowOfA = pageB.getByRole('listitem').filter({ hasText: nameA })
      await expect(friendRowOfA).toBeVisible()
      await expect(friendRowOfA.getByRole('button', { name: 'Challenge' })).toBeVisible()

      // ---- REQ-1402: direct challenge (User A -> User B) -------------------
      // User A is still on B's UserStatsScreen from the friend-request step
      // above — navigate back to Friends to reach FriendRow's own
      // "Challenge" button (FriendsTab.tsx's "My friends" section).
      await pageA.getByRole('button', { name: /^Friends/ }).click()
      const friendRowOfBOnA = pageA.getByRole('listitem').filter({ hasText: nameB })
      await expect(friendRowOfBOnA).toBeVisible()
      await friendRowOfBOnA.getByRole('button', { name: 'Challenge' }).click()
      await expect(pageA.getByText('Challenge sent.')).toBeVisible()

      await pageB.getByRole('tab', { name: 'Challenges' }).click()
      await expect(pageB.getByText(`${nameA} challenged you`)).toBeVisible()
      await pageB.getByRole('button', { name: 'Accept' }).click()
      await expect(pageB.getByText('Match started!')).toBeVisible()
      await pageB.getByRole('button', { name: 'View your matches' }).click()

      // ---- REQ-1403: exercise the opt-in call itself -----------------------
      // Out of scope for E2E, deliberately not attempted here: observing the
      // 12-hour pairing window/expiry or an actual sweep-job pairing — no
      // realistic way to fast-forward real time in a Playwright run.
      // Backend coverage for that already exists per
      // docs/requirements-document.md's REQ-1403 status note
      // (MatchmakingSweepServiceTests.cs/MatchmakingEndpointTests.cs), so
      // nothing new is needed there. This spec's only job for REQ-1403 is
      // proving the opt-in action itself works through the real UI.
      // User B is currently on the "Matches" tab (from "View your matches"
      // above) — switch to Matchmaking there, which is at least as simple
      // as switching User A's own still-"Friends"-tabbed session.
      await pageB.getByRole('tab', { name: 'Matchmaking' }).click()
      await pageB.getByRole('button', { name: 'Opt in' }).click()
      await expect(pageB.getByText("You're in the matchmaking pool until ")).toBeVisible()

      // ---- Play the challenge match to resolution (REQ-1417/1418) ---------
      const seed = await seedConnectPlayers(request)

      // User A discovers the match via their own Matches tab — still on the
      // "Friends" tab from the challenge-send step above.
      await pageA.getByRole('tab', { name: 'Matches' }).click()
      await expect(pageA.getByText('Awaiting target picks')).toBeVisible()
      await pageA.getByRole('button', { name: 'View match' }).click()

      // User B: back to Matches (left on Matchmaking above) to open the same
      // match.
      await pageB.getByRole('tab', { name: 'Matches' }).click()
      await expect(pageB.getByText('Awaiting target picks')).toBeVisible()
      await pageB.getByRole('button', { name: 'View match' }).click()

      // ---- Target-pick phase (mirrors play-connect.spec.ts's own sequencing,
      // see connect-playthrough.ts's submitTargetPick for why there's no
      // shared post-submit assertion) --------------------------------------
      await submitTargetPick(pageA, seed.targetPlayerAName)
      await expect(pageA.getByText(`Current pick: ${seed.targetPlayerAName}`)).toBeVisible()

      await submitTargetPick(pageB, seed.targetPlayerBName)
      await expect(pageB.getByText('Build your chain')).toBeVisible()

      // User A's screen only learns the match started via a fresh mount —
      // same re-open technique play-connect.spec.ts uses.
      await pageA.getByRole('button', { name: /Back to matches/ }).click()
      await pageA.getByRole('tab', { name: 'Ongoing' }).click()
      await pageA.getByRole('button', { name: 'View match' }).click()
      await expect(pageA.getByText('Build your chain')).toBeVisible()

      // ---- Chain-building to resolution ------------------------------------
      await submitClosingChainStep(pageA, seed.connectorPlayerName)
      await expect(pageA.getByText('You have finished their chain.')).toBeVisible({ timeout: 20_000 })

      await submitClosingChainStep(pageB, seed.connectorPlayerName)
      // User B's closing step was the second terminal-reaching submission,
      // so resolution ran inline in that same request — same "draw" outcome
      // play-connect.spec.ts's identical fixture produces (equal
      // 1-connector/zero-penalty scores on both sides).
      await expect(pageB.getByText("It's a draw.")).toBeVisible()

      // User A's own screen only learns of resolution via a fresh mount.
      await pageA.getByRole('button', { name: /Back to matches/ }).click()
      await pageA.getByRole('tab', { name: 'Completed' }).click()
      await pageA.getByRole('button', { name: 'View match' }).click()
      await expect(pageA.getByText("It's a draw.")).toBeVisible()

      // ---- REQ-1418: opponent's chain visible after resolution -------------
      // This is the assertion this spec is actually here to prove — not in
      // play-connect.spec.ts's API-seeded-friendship flow, but in a genuine
      // A-sent-the-request/B-accepted/A-challenged/B-accepted flow. Same
      // `.connect-match__chain-club` scoped-locator technique
      // play-connect.spec.ts uses, to avoid Playwright strict-mode
      // collisions with the closing-step's own club text (see that file's
      // own REQ-1418 comment for the full cross-over explanation).
      await expect(pageA.getByText('Opponent’s chain')).toBeVisible()
      const opponentChainClubOnA = pageA
        .locator('.connect-match__chain-club')
        .filter({ hasText: `${seed.clubOverlappingWithB}, 2015-2017` })
      await expect(opponentChainClubOnA).toBeVisible()

      await expect(pageB.getByText('Opponent’s chain')).toBeVisible()
      const opponentChainClubOnB = pageB
        .locator('.connect-match__chain-club')
        .filter({ hasText: `${seed.clubOverlappingWithA}, 2010-2012` })
      await expect(opponentChainClubOnB).toBeVisible()
    } finally {
      await contextA.close()
      await contextB.close()
    }
  })
})
