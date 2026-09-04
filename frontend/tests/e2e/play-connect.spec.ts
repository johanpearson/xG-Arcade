import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-218's own accept criterion: "Vitest + Playwright E2E covering a full
// match happy path (challenge -> both picks -> chain to completion ->
// resolution)." Unlike every other file in this directory (play-grid.spec.ts/
// play-path.spec.ts/etc.), xG Connect is a genuine 1-vs-1 game — this is the
// first spec in this repo that needs TWO independent, simultaneously
// authenticated sessions racing the same server-side match, not one player
// working through a single-player round. Two separate `browser.newContext()`
// instances (see the test body below) give each player their own isolated
// storage/cookies, the same way two different browsers/devices would really
// connect — a single shared `page` (this file's Playwright `test.describe`
// fixture) can only ever represent one logged-in session at a time.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// Matches backend/src/XGArcade.Api/Connect/InternalConnectTestDataEndpoints.cs's
// SeedConnectPlayersResponse record exactly (System.Text.Json's default
// camelCase policy). See that file's own top-of-file comment for exactly
// why this endpoint exists, including the real cross-boundary id-space bug
// (REQ-1404, now fixed on both backend and frontend) it originally had to
// work around before target-pick resolution moved to name-based lookup —
// this endpoint's PlayerNameIndex seeding step remains useful afterward for
// an unrelated reason, noted again at this spec's own target-pick step below.
interface SeedConnectPlayersResponse {
  targetPlayerAName: string
  targetPlayerBName: string
  connectorPlayerName: string
  clubOverlappingWithA: string
  clubOverlappingWithB: string
}

// Matches AuthController.Me's MeResponse record (backend/src/XGArcade.Api/
// Auth/AuthController.cs) — only `id` is used here.
interface MeResponse {
  id: string
}

// Same "one continuous playthrough" serial-mode precedent as play-grid.spec.ts/
// play-path.spec.ts — this file happens to have only one test today, but a
// generous timeout (two full sign-ups, two friend-request round trips, a
// full match, and this file's own chat step waiting out up to two 15s poll
// ticks) costs nothing and protects against flakiness the same way.
test.describe.configure({ mode: 'serial', timeout: 120_000 })

test.describe('REQ-1402/1404/1405/1406/1408/1409/1410: xG Connect full match happy path', () => {
  // REQ-701/REQ-806's real-signup-endpoint convention (see play-grid.spec.ts/
  // play-path.spec.ts's own identical helper) — a fresh, unique @test.invalid
  // account per player, created and auto-logged-in through the real UI,
  // landing on GameSelectScreen ("Choose a game").
  async function signUpNewConnectPlayer(page: Page, displayName: string, email: string): Promise<void> {
    await stubTurnstile(page)
    await page.goto('/')
    await page.getByRole('button', { name: 'Log in or sign up' }).click()
    await page.getByRole('tab', { name: 'Sign up' }).click()
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password', { exact: true }).fill('password123')
    await page.getByLabel('Confirm password').fill('password123')
    await page.getByLabel('Display name').fill(displayName)
    await page.getByLabel(/at least 16 years old/).check()
    await page.getByRole('button', { name: 'Create account' }).click()

    await expect(page.getByText('Choose a game')).toBeVisible()
  }

  // A second, API-only login for the SAME account the UI signup above just
  // created (same email/password) — purely to get a Bearer token for direct
  // setup calls below. Same "probe login via the request context" shape as
  // play-path.spec.ts's clearAnyExistingActivePathRound helper, just reused
  // for a real, already-signed-up player instead of a throwaway probe.
  async function loginForApi(request: APIRequestContext, email: string): Promise<string> {
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `login failed: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }
    return accessToken
  }

  async function fetchOwnUserId(request: APIRequestContext, accessToken: string): Promise<string> {
    const meResponse = await request.get(`${API_BASE_URL}/auth/me`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    })
    expect(meResponse.ok(), `GET /auth/me failed: ${meResponse.status()}`).toBeTruthy()
    const me = (await meResponse.json()) as MeResponse
    return me.id
  }

  test('REQ-1402/1404/1405/1406/1408/1409/1410: challenge, both target picks, chain to completion, resolution, chat', async ({
    browser,
    request,
  }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const emailA = `test-connect-a-${tag}@test.invalid`
    const emailB = `test-connect-b-${tag}@test.invalid`
    const nameA = `Connect Player A ${tag}`
    const nameB = `Connect Player B ${tag}`

    // ---- Two independent, simultaneously authenticated sessions --------
    // Unlike this file's own single-`page` fixture equivalent in every
    // other spec in this directory (auto-closed by Playwright per test), a
    // context created directly via `browser.newContext()` is only otherwise
    // cleaned up when the whole worker's shared `browser` itself tears
    // down — the `finally` block at the end of this test closes both
    // explicitly so two never-reused sessions don't linger for the rest of
    // the worker's lifetime.
    const contextA = await browser.newContext()
    const contextB = await browser.newContext()
    try {
      const pageA = await contextA.newPage()
      const pageB = await contextB.newPage()

      await signUpNewConnectPlayer(pageA, nameA, emailA)
      await signUpNewConnectPlayer(pageB, nameB, emailB)

      // ---- Friending (REQ-1401), seeded directly via the real API --------
      // REQ-1401 already has full, dedicated Unit/API coverage
      // (FriendServiceTests.cs/FriendEndpointTests.cs) — this spec's own
      // subject is REQ-1402+ (challenge through resolution/chat), not
      // friending itself. The UI's only entry point into sending a friend
      // request (SendFriendRequestAction, reached from another player's stats
      // page) needs a leaderboard row to click through, which needs an
      // unrelated round played first — seeding the precondition directly is
      // both faster and more reliable than that detour, consistent with this
      // repo's existing "seed via the real write path, drive the REQ actually
      // under test through the UI" split (see e.g. seed-guessable-round's own
      // doc comment for the same reasoning applied to xG Grid/xG Path).
      const accessTokenA = await loginForApi(request, emailA)
      const accessTokenB = await loginForApi(request, emailB)
      const userIdB = await fetchOwnUserId(request, accessTokenB)

      const sendFriendRequestResponse = await request.post(`${API_BASE_URL}/friends/requests`, {
        headers: { Authorization: `Bearer ${accessTokenA}` },
        data: { recipientUserId: userIdB },
      })
      expect(sendFriendRequestResponse.ok(), `send friend request failed: ${sendFriendRequestResponse.status()}`).toBeTruthy()
      const friendRequest = (await sendFriendRequestResponse.json()) as { id: string }

      const acceptFriendRequestResponse = await request.post(
        `${API_BASE_URL}/friends/requests/${friendRequest.id}/accept`,
        { headers: { Authorization: `Bearer ${accessTokenB}` } },
      )
      expect(
        acceptFriendRequestResponse.ok(),
        `accept friend request failed: ${acceptFriendRequestResponse.status()}`,
      ).toBeTruthy()

      // ---- Deterministic target/connector players (REQ-1404/1406) --------
      // See InternalConnectTestDataEndpoints.cs's own top-of-file comment for
      // exactly how these three players/two clubs are constructed so that (a)
      // Target A and Target B are NOT trivially connected, and (b) the one
      // connector closes either target's one-step chain symmetrically. It
      // also seeds a PlayerNameIndex row per target player so
      // /players/autocomplete has something to suggest for the target-pick
      // step below to select through the real UI — target-pick resolution
      // itself is now by name (COMP-06), not by that row's PlayerId, since
      // REQ-1404's id-space mismatch bug was fixed. No live Wikidata
      // reachability is needed anywhere in this spec.
      const seedResponse = await request.post(`${API_BASE_URL}/internal/test-data/seed-connect-players`)
      expect(seedResponse.ok(), `seed-connect-players failed: ${seedResponse.status()}`).toBeTruthy()
      const seed = (await seedResponse.json()) as SeedConnectPlayersResponse

      // ---- Challenge send (REQ-1402), via the real UI ---------------------
      // The "Friends" nav entry is a plain, unchanging "Friends" label
      // (REQ-1411's combined pending count moved to the header's own
      // NotificationBadge, not this button, in the 2026-09-03 redesign) — a
      // regex still works fine here and costs nothing extra.
      await pageA.getByRole('button', { name: /^Friends/ }).click()
      // FriendsScreen.tsx defaults to its "Friends" tab on mount — User B is
      // the only row in "My friends" (just-accepted above), so no need to
      // search it out by name/id text first.
      await pageA.getByRole('button', { name: 'Challenge' }).click()
      await expect(pageA.getByText('Challenge sent.')).toBeVisible()

      // ---- Challenge accept (REQ-1402), via the real UI -------------------
      await pageB.getByRole('button', { name: /^Friends/ }).click()
      await pageB.getByRole('tab', { name: 'Challenges' }).click()
      await expect(pageB.getByText(`${nameA} challenged you`)).toBeVisible()
      await pageB.getByRole('button', { name: 'Accept' }).click()
      await expect(pageB.getByText('Match started!')).toBeVisible()
      await pageB.getByRole('button', { name: 'View your matches' }).click()

      // MatchesTab.tsx: exactly one row now exists for User B, freshly
      // created (REQ-1402's own "accepting creates a match" transition) and
      // therefore still AwaitingTargetPicks.
      await expect(pageB.getByText('Awaiting target picks')).toBeVisible()
      await pageB.getByRole('button', { name: 'View match' }).click()

      // ---- User A discovers the same match via their own Matches tab -----
      // User A is still on the "Friends" tab of the same, still-mounted
      // FriendsScreen from the challenge-send step above — no page reload
      // needed, just switch tabs.
      await pageA.getByRole('tab', { name: 'Matches' }).click()
      await expect(pageA.getByText('Awaiting target picks')).toBeVisible()
      await pageA.getByRole('button', { name: 'View match' }).click()

      // ---- Target-pick phase (REQ-1404) -----------------------------------
      // TargetPickPanel.tsx requires selecting a real `/players/autocomplete`
      // suggestion before "Set target pick" is enabled at all — this is why
      // InternalConnectTestDataEndpoints.cs seeds a PlayerNameIndex row for
      // each target player (see that file's top-of-file comment). The
      // submission itself sends the selected suggestion's NAME, resolved
      // server-side against Player/COMP-06 (REQ-1404's id-space mismatch fix)
      // — this is now the same real path a genuine, Wikidata-imported player
      // selection would take, not a test-only workaround.
      // Deliberately no shared post-submit assertion inside this helper: the
      // UI genuinely diverges after submitting depending on whether this is
      // the first or the completing (second) target pick (see
      // ConnectTargetPickService.SubmitTargetPickAsync — both rows only flip
      // `locked` together, atomically, on the SECOND submission). The first
      // submitter's own TargetPickPanel re-renders its still-unlocked form
      // ("Current pick: ... — you can change it..."); the completing
      // submitter's own match flips to Active in the same request, so their
      // next refetch (via onSubmitted) already unmounts TargetPickPanel
      // entirely in favour of ChainBuilder — TargetPickPanel's own `locked`
      // branch ("Your target: ...") is therefore never reachable in the
      // browser for either player. Each call site below asserts what its
      // own player's screen actually shows next; Playwright's own
      // auto-retrying `expect(...).toBeVisible()` already waits out the
      // submit/refetch round trip, so no extra assertion is needed here to
      // "confirm" the submission landed.
      async function submitTargetPick(page: Page, name: string): Promise<void> {
        await page.getByLabel('Target player name').fill(name)
        await page.getByRole('option', { name }).click()
        await page.getByRole('button', { name: 'Set target pick' }).click()
      }

      await submitTargetPick(pageA, seed.targetPlayerAName)
      // User A is the FIRST submitter here — their own pick isn't locked yet
      // (TargetPickPanel.tsx's non-locked branch), so the form re-renders
      // with "Current pick: <name> — you can change it until your opponent
      // also picks." (never "Your target: ..."/"Waiting for your
      // opponent..." — that's the `locked` branch, unreachable for A).
      // Playwright's getByText concatenates all of a matched element's own
      // text nodes (including ones split across an inline <strong>), so a
      // plain substring across that boundary matches correctly without a
      // regex — same technique this file already used (incorrectly, on the
      // wrong branch) before this fix.
      await expect(pageA.getByText(`Current pick: ${seed.targetPlayerAName}`)).toBeVisible()
      await expect(pageA.getByText('you can change it until your opponent also picks.')).toBeVisible()

      // The second (completing) selection both locks User B's own pick AND
      // starts the match immediately (REQ-1405) — asserted directly via
      // ChainBuilder.tsx's own "Build your chain" heading appearing on User
      // B's screen right after this submission (their own onSubmitted
      // refetch), with no further wait needed.
      await submitTargetPick(pageB, seed.targetPlayerBName)
      await expect(pageB.getByText('Build your chain')).toBeVisible()

      // User A's screen only learns the match started via its own next 15s
      // poll or a fresh mount — re-opening the match (back to the list, then
      // in again) forces an immediate refetch (MatchScreen.tsx's own
      // useAuthedFetch mount effect) instead of waiting out or racing that
      // poll, the fastest and least flaky way to observe this transition.
      await pageA.getByRole('button', { name: /Back to matches/ }).click()
      await expect(pageA.getByText('Active')).toBeVisible()
      await pageA.getByRole('button', { name: 'View match' }).click()
      await expect(pageA.getByText('Build your chain')).toBeVisible()

      // ---- Chain-building to completion (REQ-1406/1407/1408) -------------
      // Both players close their own one-connector chain in a single step:
      // User A's chain starts from Target A (their own pick) and closes
      // against Target B (the OTHER target, per REQ-1406's own rule); User
      // B's starts from Target B and closes against Target A. Same connector
      // player both times, by this spec's own seed-connect-players design.
      // Equal 1-connector/zero-penalty scores on both sides resolve as a draw
      // (REQ-1409's equal-score branch) — a concrete, assertable outcome on
      // BOTH browser contexts below, not just one.
      // Design change (2026-09-04, REQ-1406, ADR-0104): the player no
      // longer types a claimed club — only the candidate name — so this
      // helper no longer takes one either; the server computes which
      // club(s) actually connect the two players.
      async function submitClosingChainStep(page: Page): Promise<void> {
        await page.getByLabel('Candidate player name').fill(seed.connectorPlayerName)
        // Captured (not awaited) BEFORE the click below, purely for
        // diagnostics on failure — recording the promise doesn't delay or
        // otherwise change the click/assert timing that follows, since
        // nothing here awaits it unless the "Connected!" assertion below
        // actually fails.
        const chainStepResponsePromise = page.waitForResponse(
          (response) => response.url().includes('/chain-steps') && response.request().method() === 'POST',
        )
        await page.getByRole('button', { name: 'Submit connector' }).click()
        // 2026-09-04 CONFIRMED root cause and fix (this assertion's own CI
        // trail — four failures across this spec's history, the fourth
        // genuine bug this E2E spec has caught): "Connected!" was
        // ORIGINALLY set from local React state the instant the POST
        // response arrived (ChainBuilder.tsx's own handleSubmit), with no
        // dependency on the follow-up refetch. That was fragile in exactly
        // one real scenario, caught with diagnostic logging on a real CI
        // run: when THIS submission is also the one that completes match
        // resolution (the submitter's opponent had already reached their
        // own terminal state first), `ConnectChainStepService
        // .SubmitChainStepAsync` resolves the match server-side INLINE in
        // the same request, so the very next `onChanged()`-triggered
        // refetch comes back `status: 'Resolved'` — MatchScreen.tsx
        // immediately swaps ChainBuilder out for MatchResolution, wiping
        // ChainBuilder's local `feedback` state, sometimes before that
        // state was ever painted at all. Real product bug, not a test
        // artifact: a real player closing the completing connector could
        // see the same zero-perceptible-time flash (or nothing).
        //
        // Fixed on both sides of that swap: ChainBuilder.tsx now derives
        // this acknowledgment from `myTerminalState.completed` (refreshed
        // props, durable across re-renders) instead of one-shot local
        // state, for the non-resolving case (this spec's User A, whose own
        // completion doesn't resolve the match since their opponent isn't
        // terminal yet); MatchResolution.tsx now shows the same
        // acknowledgment itself, derived from the same field in the
        // resolved-match payload, for the resolving case (User B below) —
        // see both components' own S-218 comments. Either way, this
        // assertion now depends on a real `GET /matches/{matchId}` round
        // trip completing (the POST's own follow-up refetch), not an
        // instantaneous local-state flip — the generous 20s timeout below
        // (this spec's existing precedent for other round-trip-sensitive
        // assertions, e.g. the chat-attribution check) covers that
        // legitimately, on top of covering the plain CI resource-
        // contention flake this wait was originally widened for.
        try {
          await expect(page.getByText('Connected! Your chain is complete.')).toBeVisible({ timeout: 20_000 })
        } catch (err) {
          const response = await chainStepResponsePromise.catch(() => null)
          const bodyText = response ? await response.text().catch(() => '<unreadable body>') : '<no response observed>'
          console.error(
            `submitClosingChainStep: "Connected!" never appeared for connector="${seed.connectorPlayerName}". ` +
              `POST /chain-steps responded ${response?.status() ?? '<none>'}: ${bodyText}`,
          )
          throw err
        }
      }

      await submitClosingChainStep(pageA)
      // User A alone has reached a terminal state so far (their own screen
      // now shows their own "finished their chain" status, replacing the
      // submission form) — but the MATCH itself is not yet resolved
      // (REQ-1405's "resolution waits for both terminal states" rule) until
      // User B also reaches one; User A's screen must not show a resolution
      // outcome yet.
      // Depends on ChainBuilder.tsx's own onChanged() refetch (a second,
      // separate GET /matches/{matchId} round trip after the closing
      // step's own POST) — same round-trip-under-load risk as
      // submitClosingChainStep's own "Connected!" check above.
      await expect(pageA.getByText('You have finished their chain.')).toBeVisible({ timeout: 20_000 })
      await expect(pageA.getByText("It's a draw.")).not.toBeVisible()

      await submitClosingChainStep(pageB)

      // ---- Resolution (REQ-1409) ------------------------------------------
      // User B's own closing step was the SECOND of the two terminal-reaching
      // submissions, so match resolution (TryResolveMatchIfBothTerminalAsync)
      // ran inline in that same request — User B's screen reflects it
      // immediately via the same onChanged refetch ChainBuilder.tsx already
      // triggers on every accepted/closing step, no further navigation
      // needed.
      await expect(pageB.getByText("It's a draw.")).toBeVisible()

      // User A's own screen only learns of the just-completed resolution via
      // its next poll or a fresh mount — same re-open technique as the
      // match-start transition above.
      await pageA.getByRole('button', { name: /Back to matches/ }).click()
      await expect(pageA.getByText('Resolved')).toBeVisible()
      await expect(pageA.getByText('Draw')).toBeVisible()
      await pageA.getByRole('button', { name: 'View match' }).click()
      await expect(pageA.getByText("It's a draw.")).toBeVisible()

      // Both scores are the 1-connector/zero-penalty minimum (REQ-1408's own
      // "lowest possible score for a completed chain" case) — never "0" and
      // never "Forfeited," since both players actually completed a chain.
      const myScoreRowA = pageA.locator('.connect-match__score-row').filter({ hasText: 'Your score' })
      await expect(myScoreRowA.getByText('1', { exact: true })).toBeVisible()
      const myScoreRowB = pageB.locator('.connect-match__score-row').filter({ hasText: 'Your score' })
      await expect(myScoreRowB.getByText('1', { exact: true })).toBeVisible()

      // REQ-1406 (design change, 2026-09-04, ADR-0104): direct end-to-end
      // proof the server-computed matched club/years render correctly, now
      // that the player never types one — InternalConnectTestDataEndpoints.cs
      // seeds the connector's overlap with each target at an identical
      // (StartYear, EndYear) pair on both sides, so the intersection is
      // exactly that range, not a computed subset.
      await expect(pageA.getByText(`${seed.clubOverlappingWithA}, 2010-2012`)).toBeVisible()
      await expect(pageB.getByText(`${seed.clubOverlappingWithB}, 2015-2017`)).toBeVisible()

      // ---- In-match chat (REQ-1410), bonus coverage given the same fixture -
      // Rendered unconditionally below every phase's own content, including a
      // resolved match (REQ-1410's own "chat remains visible/readable" rule)
      // — exercised here, after resolution, for exactly that reason.
      await pageA.getByLabel('Chat message').fill('gg, well played')
      await pageA.getByRole('button', { name: 'Send message' }).click()
      await expect(pageA.getByText('gg, well played')).toBeVisible()

      await pageB.getByLabel('Chat message').fill('gg to you too')
      await pageB.getByRole('button', { name: 'Send message' }).click()
      await expect(pageB.getByText('gg to you too')).toBeVisible()

      // Each side sees the OTHER's message attributed to their own real
      // display name (MatchChat.tsx renders ChatMessageResponse's
      // `senderDisplayName` directly, viewerUserId-based split — their own
      // message shows as "You" instead, already implicitly covered by the
      // plain text assertions above). The receiving side only learns of the
      // other's message via its own next 15s poll tick, not instantly — a
      // generous explicit timeout lets Playwright's auto-waiting retry this
      // assertion across that poll instead of racing it.
      //
      // Scoped to MatchChat.tsx's own sender span (.connect-match__chat-sender),
      // not a bare page-wide getByText: by this point in the test, A and B are
      // already friends (seeded above), and FriendsTab.tsx renders a "My
      // friends" row for the other player using this exact same display-name
      // text, mounted-but-hidden under FriendsScreen.tsx's deliberate
      // "Friends/Challenges/Matchmaking stay mounted" convention (see that
      // file's own top-of-file comment) rather than removed from the DOM — a
      // bare getByText matches hidden elements too, so it resolves to both
      // that row AND this chat message and trips Playwright's strict mode.
      // Each side has exactly one chat message from the other player in this
      // flow (one message sent per side), so this scoped locator resolves to
      // exactly one element.
      await expect(
        pageA.locator('.connect-match__chat-sender').getByText(nameB),
      ).toBeVisible({ timeout: 20_000 })
      await expect(
        pageB.locator('.connect-match__chat-sender').getByText(nameA),
      ).toBeVisible({ timeout: 20_000 })
    } finally {
      await contextA.close()
      await contextB.close()
    }
  })
})
