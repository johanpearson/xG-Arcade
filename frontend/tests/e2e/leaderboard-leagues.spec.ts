import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-244 (docs/backlog.md): the first spec to actually open the leaderboard
// or exercise custom leagues — every other spec in this directory stops at
// "play the game." Covers REQ-401 (global league auto-membership, exercised
// implicitly — it's what makes a signed-up player's row show up at all)/
// REQ-404 (the ranking read)/REQ-408 (browsing one specific closed round's
// locked leaderboard)/REQ-405 (time-window resolutions, a lighter follow-on
// per this story's own scoping)/REQ-402 (create a custom league)/REQ-403
// (join one via invite code)/REQ-701/REQ-806/REQ-807 (the same signup and
// round-seed/force-close test-data conventions every other spec here
// already uses).
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

interface SeedGuessableRoundResponse {
  roundId: string
  cellId: string
  correctPlayerName: string
  alternateCorrectPlayerName: string
}

interface ClosedRoundSummaryApi {
  roundId: string
  closedAt: string
}

interface ClosedRoundListResponseApi {
  rounds: ClosedRoundSummaryApi[]
}

// REQ-303/REQ-807 (see play-grid.spec.ts's own extensive comment above its
// `clearAnyExistingActiveRound` for the full history): `GET /rounds/current`
// resolves "the" one Active round for the whole xG Grid `GameKey` — a single
// global resource, not scoped per caller or per spec file. play-grid.spec.ts
// already seeds/closes xG Grid rounds the same way this file needs to, and
// playwright.config.ts's `fullyParallel: true` (no worker cap) means that
// file's own tests can genuinely be mid-round in another worker while this
// file runs. This is a real, pre-existing architectural gap (a single
// "Active round for this GameKey" resource with no per-test isolation), not
// something this file can fix on its own — see this story's own brief for
// why forcing `workers: 1` is out of scope here. What this file does to
// minimize (not eliminate) that exposure, mirroring play-grid.spec.ts's own
// defenses exactly:
//   (a) `test.describe.configure({ mode: 'serial' })` — this file's own
//       tests never run concurrently with each other,
//   (b) a `beforeAll` that clears any leftover Active round the same way
//       `clearAnyExistingActiveRound` below does (for state left over from a
//       prior local run against a persisted dev DB — ci.yml's fresh
//       per-run Postgres container wouldn't have this problem),
//   (c) every round this file seeds is force-closed as soon as this file is
//       done needing it Active — via the API only, no intervening browser
//       interaction — since (unlike play-grid.spec.ts) nothing in this
//       story needs to observe a round while it's still live.
test.describe.configure({ mode: 'serial', timeout: 60_000 })

test.describe('REQ-401/402/403/404/405/408/701/806/807: leaderboard and league flows', () => {
  // Same "sweep away any pre-existing Active round" probe as
  // play-grid.spec.ts's own `clearAnyExistingActiveRound` — see that file's
  // comment for the full reasoning (a hardcoded hit against REQ-701's
  // display-name uniqueness check on a local rerun, etc.). Duplicated here
  // rather than imported: each spec file in this directory is self-
  // contained, matching the existing convention (no shared test-helper
  // module between play-grid.spec.ts/play-path.spec.ts/play-connect.spec.ts
  // today).
  async function clearAnyExistingActiveRound(request: APIRequestContext): Promise<void> {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-probe-${tag}@test.invalid`
    await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', confirmPassword: 'password123', displayName: `Probe ${tag}`, ageConfirmed: true, captchaToken: 'e2e-test-token' },
    })
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `probe login failed: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }

    for (let attempt = 0; attempt < 10; attempt += 1) {
      const roundResponse = await request.get(`${API_BASE_URL}/rounds/current`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      })
      if (roundResponse.status() === 404) return
      expect(roundResponse.ok(), `GET /rounds/current failed: ${roundResponse.status()}`).toBeTruthy()
      const { roundId } = (await roundResponse.json()) as { roundId: string }
      const closeResponse = await request.post(`${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`)
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }
    throw new Error('clearAnyExistingActiveRound: too many pre-existing Active rounds to clear.')
  }

  test.beforeAll(async ({ request }) => {
    await clearAnyExistingActiveRound(request)
  })

  // REQ-701/REQ-806: a fresh, unique @test.invalid account, created through
  // the real signup endpoint, then logged in through the real login
  // endpoint — same convention as play-grid.spec.ts's own
  // `signUpAndLoginViaApi`.
  async function signUpAndLoginViaApi(
    request: APIRequestContext,
    email: string,
    displayName: string,
  ): Promise<string> {
    const signupResponse = await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', confirmPassword: 'password123', displayName, ageConfirmed: true, captchaToken: 'e2e-test-token' },
    })
    expect(signupResponse.ok(), `signup failed: ${signupResponse.status()}`).toBeTruthy()

    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `login failed: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }
    return accessToken
  }

  async function submitGuessViaApi(
    request: APIRequestContext,
    accessToken: string,
    roundId: string,
    cellId: string,
    submittedName: string,
  ): Promise<{ isCorrect: boolean }> {
    const response = await request.post(`${API_BASE_URL}/rounds/${roundId}/cells/${cellId}/guesses`, {
      headers: { Authorization: `Bearer ${accessToken}` },
      data: { submittedName },
    })
    expect(response.ok(), `submit guess failed: ${response.status()}`).toBeTruthy()
    return response.json()
  }

  async function forceCloseRound(request: APIRequestContext, roundId: string): Promise<void> {
    const closeResponse = await request.post(`${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`)
    expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
  }

  // REQ-408: fetches the just-closed round's own `closedAt` value straight
  // from the API (rather than assuming it's first/topmost in the UI's own
  // round list) — this is what lets the UI step below select the exact
  // round this test seeded via an exact accessible-name match
  // (`PastRoundsLeaderboard.tsx` renders each round-list button's text as
  // literally `Closed {round.closedAt}`, the same raw ISO string this
  // fetch and that component both read off the same JSON response), rather
  // than relying on list position — which the shared-Active-round comment
  // above already flags as unsafe given concurrent spec files can close
  // their own xG Grid rounds at any time. A `closedAt` collision between two
  // independently-closed rounds is effectively impossible (real timestamp
  // precision), so this is a robust, not just convenient, way to find the
  // right list entry.
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
          'their own xG Grid rounds; see this file\'s shared-Active-round comment above.',
      )
    }
    return match.closedAt
  }

  // REQ-701: signs up a fresh player through the real UI (same shape as
  // play-grid.spec.ts's own `signUpNewPlayer`/play-connect.spec.ts's
  // `signUpNewConnectPlayer`), landing on GameSelectScreen — HeaderNav (and
  // therefore the "Leaderboard"/"Leagues" entries this file actually needs)
  // renders as soon as `accessToken` is set, regardless of which screen is
  // showing (App.tsx), so no game needs to be selected afterward.
  async function signUpNewPlayerViaUi(page: Page, email: string, displayName: string): Promise<void> {
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
    await expect(page.getByRole('button', { name: 'Leaderboard' })).toBeVisible()
  }

  // Logs an already-signed-up (via the API) account into the real UI — same
  // shape as play-grid.spec.ts's REQ-401 test's own inline login step.
  async function logInViaUi(page: Page, email: string): Promise<void> {
    await stubTurnstile(page)
    await page.goto('/')
    await page.getByRole('button', { name: 'Log in or sign up' }).click()
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill('password123')
    await page.getByRole('button', { name: 'Log in' }).click()
    await expect(page.getByRole('button', { name: 'Leaderboard' })).toBeVisible()
  }

  test('REQ-401/404/408/701/806/807: a seeded round closes and its locked result shows under the leaderboard\'s Previous Rounds scope', async ({
    page,
    request,
  }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-prevrounds-${tag}@test.invalid`
    const displayName = `Prev Rounds ${tag}`

    const seed = await request.post(`${API_BASE_URL}/internal/test-data/seed-guessable-round`)
    expect(seed.ok(), `seed-guessable-round failed: ${seed.status()}`).toBeTruthy()
    const { roundId, cellId, correctPlayerName } = (await seed.json()) as SeedGuessableRoundResponse

    const accessToken = await signUpAndLoginViaApi(request, email, displayName)
    const guess = await submitGuessViaApi(request, accessToken, roundId, cellId, correctPlayerName)
    expect(guess.isCorrect).toBe(true)

    // Close immediately after the one guess this test needs — see the
    // shared-Active-round comment above for why this window is kept as
    // short as possible.
    await forceCloseRound(request, roundId)
    const closedAt = await fetchClosedAtForRound(request, accessToken, roundId)

    // View it through the real UI (REQ-408's own "Test level: ... UI"),
    // logged in as the same player who submitted the guess above.
    await logInViaUi(page, email)
    await page.getByRole('button', { name: 'Leaderboard' }).click()
    await page.getByRole('tab', { name: 'Previous Rounds' }).click()

    const roundButton = page.getByRole('button', { name: closedAt })
    await expect(roundButton).toBeVisible({ timeout: 10_000 })
    await roundButton.click()

    // REQ-204/ADR-0020/ADR-0021 (same reasoning play-grid.spec.ts's own
    // REQ-401 test already documents in full): the sole correct guesser of
    // a cell has zero *other* correct guessers sharing their exact answer,
    // so they're trivially 100% unique — the BEST possible score under
    // golf-style scoring, which locks in at 0 points, not
    // `ScoringRules.MaxPointsPerCell`.
    const row = page.getByRole('listitem').filter({ hasText: displayName })
    await expect(row).toBeVisible()
    await expect(row.getByText('0 pts')).toBeVisible()
    await expect(row.getByText('you')).toBeVisible()
  })

  test('REQ-405: the leaderboard\'s Time Windows scope renders without error after switching to Week and Month', async ({
    page,
    request,
  }) => {
    // REQ-405's round/week/month/year windows are calendar-aligned across
    // every closed xG Grid round in that period, not just one this test
    // seeds — concurrent spec files' own closed rounds (real player
    // guesses/points) may legitimately show up here too. This story's own
    // brief is explicit that this follow-on only asserts the view loads
    // without erroring, never specific rows/points, for exactly that
    // reason. No guess is submitted against this round — it exists only so
    // "reuses the same seeded-round setup" (this story's own wording) has a
    // genuinely closed round backing it, same seed/close-immediately
    // helpers as the test above.
    const seed = await request.post(`${API_BASE_URL}/internal/test-data/seed-guessable-round`)
    expect(seed.ok(), `seed-guessable-round failed: ${seed.status()}`).toBeTruthy()
    const { roundId } = (await seed.json()) as SeedGuessableRoundResponse
    await forceCloseRound(request, roundId)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-window-${tag}@test.invalid`
    const displayName = `Window Player ${tag}`
    await signUpNewPlayerViaUi(page, email, displayName)

    await page.getByRole('button', { name: 'Leaderboard' }).click()
    await page.getByRole('tab', { name: 'Time Windows' }).click()

    async function switchToWindowAndExpectNoError(label: string): Promise<void> {
      const tab = page.getByRole('tab', { name: label, exact: true })
      await tab.click()
      await expect(tab).toHaveAttribute('aria-selected', 'true')
      // "Renders" == the loading state resolves (to either rows or the
      // empty-window message) without landing on the error branch —
      // exactly what this story's brief asks for, no more.
      await expect(page.getByText('Loading this window’s leaderboard…')).not.toBeVisible({
        timeout: 10_000,
      })
      await expect(page.locator('.leaderboard-screen__status--error')).toHaveCount(0)
    }

    await switchToWindowAndExpectNoError('Week')
    await switchToWindowAndExpectNoError('Month')
  })

  test('REQ-402/403: creating a custom league produces an invite code that a second player can join by', async ({
    browser,
  }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const emailA = `test-league-a-${tag}@test.invalid`
    const emailB = `test-league-b-${tag}@test.invalid`
    const nameA = `League Creator ${tag}`
    const nameB = `League Joiner ${tag}`
    const leagueName = `E2E League ${tag}`

    // Two independent, simultaneously authenticated sessions — same
    // `browser.newContext()` pattern play-connect.spec.ts already
    // established for a scenario that genuinely needs two distinct
    // identities interacting (here: player A's invite code has to be read
    // back out of player A's own session and handed to player B's).
    const contextA = await browser.newContext()
    const contextB = await browser.newContext()
    try {
      const pageA = await contextA.newPage()
      const pageB = await contextB.newPage()

      await signUpNewPlayerViaUi(pageA, emailA, nameA)
      await signUpNewPlayerViaUi(pageB, emailB, nameB)

      // ---- REQ-402: player A creates the league ------------------------
      await pageA.getByRole('button', { name: 'Leagues' }).click()
      await pageA.getByLabel('League name').fill(leagueName)
      await pageA.getByRole('button', { name: 'Create league' }).click()

      const rowA = pageA.getByRole('listitem').filter({ hasText: leagueName })
      await expect(rowA).toBeVisible()
      const codeTextA = await rowA.getByText(/^Code: /).innerText()
      const inviteCode = codeTextA.replace(/^Code:\s*/, '').trim()
      // REQ-402: "a unique 6-character invite_code" — a real assertion on
      // the shape of what was produced, not just that some text exists.
      expect(inviteCode).toHaveLength(6)

      // ---- REQ-403: player B joins via that invite code -----------------
      await pageB.getByRole('button', { name: 'Leagues' }).click()
      await pageB.getByLabel('Invite code').fill(inviteCode)
      await pageB.getByRole('button', { name: 'Join league' }).click()

      const rowB = pageB.getByRole('listitem').filter({ hasText: leagueName })
      await expect(rowB).toBeVisible()
      await expect(rowB.getByText(`Code: ${inviteCode}`)).toBeVisible()
    } finally {
      await contextA.close()
      await contextB.close()
    }
  })

  test('REQ-403: an invalid invite code shows a clear inline error and creates no membership', async ({ page }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-league-invalid-${tag}@test.invalid`
    const displayName = `Bad Code Player ${tag}`
    await signUpNewPlayerViaUi(page, email, displayName)

    await page.getByRole('button', { name: 'Leagues' }).click()
    await expect(page.getByText("You're not in any custom leagues yet.")).toBeVisible()

    // Six uppercase characters, matching the shape of a real invite code
    // (InviteCode alphabet excludes visually-ambiguous characters, but
    // this doesn't need to be a member of that exact alphabet to be a
    // guaranteed miss — it just needs to not exist).
    await page.getByLabel('Invite code').fill('ZZZZZZ')
    await page.getByRole('button', { name: 'Join league' }).click()

    await expect(page.getByRole('alert')).toBeVisible()
    // No membership was created — the empty state is unchanged.
    await expect(page.getByText("You're not in any custom leagues yet.")).toBeVisible()
  })
})
