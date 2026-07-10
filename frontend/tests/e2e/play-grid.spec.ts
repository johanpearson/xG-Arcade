import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

// Matches the pattern already established for the backend base URL (see
// app-loads.spec.ts's use of the frontend's own VITE_API_BASE_URL indirectly
// via the app under test) and ci.yml's E2E step, which sets this exact env
// var; localhost:8080 mirrors ci.yml's API port when run locally without it.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

interface SeedGuessableRoundResponse {
  roundId: string
  cellId: string
  correctPlayerName: string
  alternateCorrectPlayerName: string
}

// REQ-303's GET /rounds/current resolves "the" currently Active round for
// the whole xG Grid game key — it has no per-caller/per-test scoping, and
// REQ-807's seed endpoint creates a brand-new Active round on every call.
// RoundRepository.GetActiveByGameKeyAsync originally had no ORDER BY, so if
// two Active rounds existed at once, which one a given request got back was
// nondeterministic — confirmed while writing this suite: a round left over
// from an earlier local run (never closed, still within its 1h window) made
// a fresh run's very first assertion fail because /rounds/current returned
// the stale round instead of the one just seeded. That query is now fixed
// (OrderByDescending(StartTime), plus a dedicated regression test — see
// CurrentRoundEndpointTests.REQ303_..._MultipleActiveRoundsForSameGame_...),
// but this suite keeps its own defense in depth rather than depending on
// that fix alone: (a) running its tests serially rather than relying on
// playwright.config.ts's project-wide fullyParallel for this file
// specifically, (b) sweeping away any pre-existing Active round before the
// first seed call (clearAnyExistingActiveRound, for state left over from a
// prior local run against the same long-lived dev DB — ci.yml's fresh
// per-run Postgres container wouldn't have this problem, but a local run
// against an already-migrated DB can), and (c) force-closing the round each
// test seeded before the next test seeds its own. Each test still seeds its
// own fresh round/cell — this is purely about never having two Active
// rounds coexist, not about tests depending on each other's data.
test.describe.configure({ mode: 'serial' })

test.describe('REQ-201/202/203/210/303/701/807: play a full grid round', () => {
  let previousRoundId: string | null = null

  // Repeatedly closes whatever round GET /rounds/current currently reports
  // as Active (there's no "list active rounds" endpoint, so this is the
  // only way to discover one) until none remains. A throwaway probe account
  // is used purely to read that endpoint, never to submit guesses.
  async function clearAnyExistingActiveRound(request: APIRequestContext): Promise<void> {
    const email = `test-probe-${Date.now()}-${Math.random().toString(36).slice(2)}@test.invalid`
    await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', displayName: 'Probe', ageConfirmed: true },
    })
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123' },
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
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }
    throw new Error('clearAnyExistingActiveRound: too many pre-existing Active rounds to clear.')
  }

  test.beforeAll(async ({ request }) => {
    await clearAnyExistingActiveRound(request)
  })

  async function seedFreshRound(request: APIRequestContext): Promise<SeedGuessableRoundResponse> {
    if (previousRoundId) {
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${previousRoundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }

    const response = await request.post(`${API_BASE_URL}/internal/test-data/seed-guessable-round`)
    expect(response.ok(), `seed-guessable-round failed: ${response.status()}`).toBeTruthy()
    const body = (await response.json()) as SeedGuessableRoundResponse
    previousRoundId = body.roundId
    return body
  }

  // REQ-701/REQ-803/REQ-806's @test.invalid convention: a fresh, unique
  // account per test, created through the real signup endpoint (never
  // seeded directly), with the required age checkbox checked. AuthScreen
  // auto-logs-in after signup (see AuthScreen.tsx's handleSubmit), so no
  // separate login step is needed here.
  // S-011: signs up and logs in purely via the API (no browser page needed)
  // — REQ-806's own convention (real signup endpoint, @test.invalid emails),
  // used here so two players' guesses can be submitted without juggling two
  // separate browser contexts for a scenario that only needs one page for
  // the leaderboard assertion itself.
  async function signUpAndLoginViaApi(
    request: APIRequestContext,
    email: string,
    displayName: string,
  ): Promise<string> {
    const signupResponse = await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', displayName, ageConfirmed: true },
    })
    expect(signupResponse.ok(), `signup failed: ${signupResponse.status()}`).toBeTruthy()

    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123' },
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
    const response = await request.post(
      `${API_BASE_URL}/rounds/${roundId}/cells/${cellId}/guesses`,
      {
        headers: { Authorization: `Bearer ${accessToken}` },
        data: { submittedName },
      },
    )
    expect(response.ok(), `submit guess failed: ${response.status()}`).toBeTruthy()
    return response.json()
  }

  async function signUpNewPlayer(page: Page): Promise<void> {
    const email = `test-${Date.now()}-${Math.random().toString(36).slice(2)}@test.invalid`

    await page.goto('/')
    await page.getByRole('tab', { name: 'Sign up' }).click()
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill('password123')
    await page.getByLabel('Display name').fill('Test Player')
    await page.getByLabel(/at least 16 years old/).check()
    await page.getByRole('button', { name: 'Create account' }).click()
  }

  // REQ-701 (account creation + auto-login) / REQ-303 (fetch the active
  // round and grid) / REQ-201 (submit a guess) / REQ-203 (immediate
  // correctness) / REQ-210 (two attempts, correct locks immediately): a
  // brand-new player logs in, sees the seeded France x Arsenal cell, gets an
  // intentionally wrong guess back as incorrect with "1 attempt left" (never
  // color/icon-only — REQ-210 requires visible text), then submits the real
  // correct player name and sees it marked correct and locked immediately,
  // with no page reload in between.
  test('REQ-701/303/201/203/210: signup, wrong guess shows incorrect + attempts left, correct guess locks the cell live', async ({
    page,
    request,
  }) => {
    const seed = await seedFreshRound(request)
    const cell = page.getByTestId(`grid-cell-${seed.cellId}`)

    await signUpNewPlayer(page)

    // REQ-303: the seeded cell's categories render as headers in the grid,
    // and the unattempted cell exposes the REQ-201 "open a cell" affordance.
    await expect(page.getByText('France')).toBeVisible()
    await expect(page.getByText('Arsenal')).toBeVisible()
    await expect(cell).toHaveAccessibleName('Guess France × Arsenal')

    // First attempt: an intentionally wrong name.
    await cell.click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await page.getByLabel('Player name').fill('Definitely Not A Real Player')
    await page.getByRole('button', { name: 'Submit guess' }).click()

    // REQ-201/203: correctness shown immediately, no reload — the dialog
    // closes itself on a successfully-accepted (even if wrong) submission.
    await expect(page.getByRole('dialog')).not.toBeVisible()
    await expect(cell.getByText('Definitely Not A Real Player')).toBeVisible()
    await expect(cell.getByText('1 attempt left')).toBeVisible()
    await expect(cell).toBeEnabled()

    // Second attempt: the real correct player name from the seed response.
    await cell.click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await expect(page.getByText('1 of 2 attempts used')).toBeVisible()
    await page.getByLabel('Player name').fill(seed.correctPlayerName)
    await page.getByRole('button', { name: 'Submit guess' }).click()

    // REQ-210: a correct answer locks the cell immediately, even though
    // only 1 of the 2 attempts was used for a wrong guess before it.
    await expect(page.getByRole('dialog')).not.toBeVisible()
    await expect(cell.getByText(seed.correctPlayerName)).toBeVisible()
    await expect(cell.getByText('live')).toBeVisible()
    await expect(cell).toBeDisabled()
  })

  // REQ-210's other lock path: locking after both attempts are used without
  // a correct answer, distinct from the correct-answer-locks-immediately
  // path above. Uses its own freshly seeded cell/round (via seedFreshRound,
  // which force-closes the previous test's round first) rather than reusing
  // any state from the test above.
  test('REQ-210: two wrong guesses in a row lock the cell as "no attempts left"', async ({
    page,
    request,
  }) => {
    const seed = await seedFreshRound(request)
    const cell = page.getByTestId(`grid-cell-${seed.cellId}`)

    await signUpNewPlayer(page)
    await expect(cell).toHaveAccessibleName('Guess France × Arsenal')

    await cell.click()
    await page.getByLabel('Player name').fill('Wrong Guess Number One')
    await page.getByRole('button', { name: 'Submit guess' }).click()
    await expect(page.getByRole('dialog')).not.toBeVisible()
    await expect(cell.getByText('1 attempt left')).toBeVisible()
    await expect(cell).toBeEnabled()

    await cell.click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await page.getByLabel('Player name').fill('Wrong Guess Number Two')
    await page.getByRole('button', { name: 'Submit guess' }).click()

    // REQ-210: both attempts used without a correct answer locks the cell
    // as incorrect — shown as visible text, never color/icon-only.
    await expect(page.getByRole('dialog')).not.toBeVisible()
    await expect(cell.getByText('Wrong Guess Number Two')).toBeVisible()
    await expect(cell.getByText('no attempts left')).toBeVisible()
    await expect(cell).toBeDisabled()
  })

  // S-011 (docs/backlog.md): REQ-204 (denominator = correct guesses only,
  // one per player)/REQ-205 (score locking)/REQ-206 (total score)/REQ-401
  // (global league auto-membership). Two players each correctly guess a
  // *different* one of the cell's two valid answers (REQ-807's seed now
  // provides two) — each is the sole correct guesser of their own answer,
  // so REQ-204's uniqueness splits 50/50 and both lock in at 50 points
  // (ScoringRules.MaxPointsPerCell = 100) once force-closed, never the 0 or
  // 100 a version that (incorrectly) counted incorrect/burned guesses in
  // the denominator would produce.
  test('REQ-204/205/206/401: two players guess, force-close locks scores, the global leaderboard shows the locked totals', async ({
    page,
    request,
  }) => {
    const seed = await seedFreshRound(request)
    // A short unique tag (not just the email) — repeated local runs against
    // a persisted dev DB would otherwise accumulate multiple "Leaderboard
    // Alex" rows and break the row locators' strict-mode single-match
    // requirement below. ci.yml's Postgres container is fresh per run, so
    // this only matters for local reruns, same reasoning as the email
    // suffix already used elsewhere in this file.
    const tag = Math.random().toString(36).slice(2, 8)
    const playerAEmail = `test-leaderboard-a-${Date.now()}-${tag}@test.invalid`
    const playerBEmail = `test-leaderboard-b-${Date.now()}-${tag}@test.invalid`
    const playerADisplayName = `Alex ${tag}`
    const playerBDisplayName = `Sam ${tag}`

    const playerAToken = await signUpAndLoginViaApi(request, playerAEmail, playerADisplayName)
    const playerBToken = await signUpAndLoginViaApi(request, playerBEmail, playerBDisplayName)

    const guessA = await submitGuessViaApi(
      request, playerAToken, seed.roundId, seed.cellId, seed.correctPlayerName,
    )
    expect(guessA.isCorrect).toBe(true)
    const guessB = await submitGuessViaApi(
      request, playerBToken, seed.roundId, seed.cellId, seed.alternateCorrectPlayerName,
    )
    expect(guessB.isCorrect).toBe(true)

    const closeResponse = await request.post(
      `${API_BASE_URL}/internal/test-data/force-close-round/${seed.roundId}`,
    )
    expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()

    // View the leaderboard as Player A, through the real UI (not the API) —
    // this is the one part of the scenario SCREEN-03 itself needs to prove.
    await page.goto('/')
    await page.getByLabel('Email').fill(playerAEmail)
    await page.getByLabel('Password').fill('password123')
    await page.getByRole('button', { name: 'Log in' }).click()
    await page.getByRole('button', { name: 'Leaderboard' }).click()

    const rowA = page.getByRole('listitem').filter({ hasText: playerADisplayName })
    await expect(rowA).toBeVisible()
    await expect(rowA.getByText('50 pts')).toBeVisible()
    await expect(rowA.getByText('you')).toBeVisible()

    const rowB = page.getByRole('listitem').filter({ hasText: playerBDisplayName })
    await expect(rowB).toBeVisible()
    await expect(rowB.getByText('50 pts')).toBeVisible()
    await expect(rowB.getByText('you')).not.toBeVisible()
  })
})
