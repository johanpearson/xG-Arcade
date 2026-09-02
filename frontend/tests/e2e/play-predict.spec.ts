import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-203: this file's structure deliberately mirrors play-grid.spec.ts's/
// play-path.spec.ts's own conventions throughout (API_BASE_URL,
// stubTurnstile usage, serial mode, clearAnyExisting*/seed*/signUp* helper
// shapes, @test.invalid emails) — see those files' own comments for the
// parts of the reasoning that are identical for xG Predict and aren't
// repeated here. Comments below focus on what's different for xG Predict.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// Matches backend/src/XGArcade.Api/Rounds/InternalRoundEndpoints.cs's
// SeedGuessablePredictRoundResponse/SeedGuessablePredictMatchResponse
// records exactly (System.Text.Json's default camelCase policy). Matches is
// ordered by KickoffUtc by that endpoint itself — never re-sorted here.
interface SeedGuessablePredictMatch {
  matchId: string
  homeTeamName: string
  awayTeamName: string
}
interface SeedGuessablePredictRoundResponse {
  roundId: string
  matches: SeedGuessablePredictMatch[]
}

// Unlike xG Grid's guesses, an xG Predict prediction never pays ADR-0018's
// live-Wikidata-lookup cost — PredictEndpoints' write path
// (XGPredictGameModule.ScoreSubmissionAsync) only ever validates and stores
// two integers against already-persisted PredictMatch rows. Same "no
// equivalent of WRONG_GUESS_TIMEOUT_MS" reasoning play-path.spec.ts's own
// comment already documents for xG Path; default Playwright expect/test
// timeouts are used throughout.

// REQ-1303's round-wide lock is entirely driven by
// PredictInstance.LockInstant (the earliest of an instance's 5 matches' own
// KickoffUtc) — GET /predict/current resolves "the" currently Active round
// for the whole xg-predict GameKey the same no-per-caller-scoping way
// REQ-303's GET /rounds/current and REQ-1203's GET /path/current do (see
// those files' own comments) — so this file needs its own "clear a leftover
// Active round" defense scoped to GET /predict/current specifically, and
// runs serially for the same defense-in-depth reasoning.
test.describe.configure({ mode: 'serial', timeout: 60_000 })

test.describe('REQ-1301/1302/1303/1304/1305/1306/410: play a full xG Predict round', () => {
  let previousRoundId: string | null = null

  // Repeatedly closes whatever round GET /predict/current currently reports
  // as Active until none remains — same "there's no list-active-rounds
  // endpoint" limitation play-grid.spec.ts's/play-path.spec.ts's own
  // equivalent helpers document. A throwaway probe account is used purely to
  // read that endpoint, never to submit predictions.
  async function clearAnyExistingActivePredictRound(request: APIRequestContext): Promise<void> {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-predict-probe-${tag}@test.invalid`
    await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', confirmPassword: 'password123', displayName: `Predict Probe ${tag}`, ageConfirmed: true, captchaToken: 'e2e-test-token' },
    })
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `probe login failed: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }

    for (let attempt = 0; attempt < 10; attempt += 1) {
      const roundResponse = await request.get(`${API_BASE_URL}/predict/current`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      })
      if (roundResponse.status() === 404) return
      expect(roundResponse.ok(), `GET /predict/current failed: ${roundResponse.status()}`).toBeTruthy()
      const { roundId } = (await roundResponse.json()) as { roundId: string }
      // REQ-806's boundary: force-close-round/{roundId} is game-agnostic
      // (IRoundCloseService.CloseRoundAsync just closes whatever round id
      // it's given) — the same endpoint play-grid.spec.ts/play-path.spec.ts
      // already rely on works unchanged for an xg-predict round.
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }
    throw new Error('clearAnyExistingActivePredictRound: too many pre-existing Active rounds to clear.')
  }

  test.beforeAll(async ({ request }) => {
    await clearAnyExistingActivePredictRound(request)
  })

  // Closes the previous test's round (if any) before seeding a fresh one —
  // same single-shared-previousRoundId convention play-grid.spec.ts's own
  // seedFreshRound uses, so at most one xg-predict round is ever Active at
  // once across this file's serial tests.
  //
  // firstKickoffMinutesFromNow controls REQ-1303's round-wide lock instant
  // directly (see InternalRoundEndpoints.cs's own doc comment on this
  // endpoint): omitted/positive seeds an open round (for viewing the slate,
  // submitting predictions, and REQ-1306's confirm-and-lock flow); negative
  // seeds an already-locked one (for REQ-1303's round-wide-lock notice).
  async function seedPredictRound(
    request: APIRequestContext,
    firstKickoffMinutesFromNow?: number,
  ): Promise<SeedGuessablePredictRoundResponse> {
    if (previousRoundId) {
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${previousRoundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }

    const url = firstKickoffMinutesFromNow === undefined
      ? `${API_BASE_URL}/internal/test-data/seed-guessable-predict-round`
      : `${API_BASE_URL}/internal/test-data/seed-guessable-predict-round?firstKickoffMinutesFromNow=${firstKickoffMinutesFromNow}`
    const response = await request.post(url)
    expect(response.ok(), `seed-guessable-predict-round failed: ${response.status()}`).toBeTruthy()
    const body = (await response.json()) as SeedGuessablePredictRoundResponse
    previousRoundId = body.roundId
    return body
  }

  // REQ-701/REQ-806's real-signup-endpoint convention (same as
  // play-grid.spec.ts's signUpNewPlayer/play-path.spec.ts's
  // signUpNewPathPlayer): a fresh, unique @test.invalid account, created and
  // auto-logged-in through the real UI. Ends on GameSelectScreen's "xG
  // Predict" tile (frontend/src/games/GameSelectScreen.tsx's
  // aria-label="xG Predict" button) and clicks through to SCREEN-14.
  async function signUpNewPredictPlayer(page: Page, displayName: string, email: string): Promise<void> {
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

    await page.getByRole('button', { name: 'xG Predict' }).click()
  }

  // Each match row (PredictMatchInput.tsx's `.predict-match-input`) carries
  // no data-testid of its own, but its two goal inputs' aria-labels embed
  // the match's own home/away team names — this file's seeded team names
  // (InternalRoundEndpoints.cs: "Predict Test Home/Away {tag} {i}") are
  // always unique per match, so filtering by homeTeamName text reliably
  // scopes to exactly one row.
  function matchContainer(page: Page, match: SeedGuessablePredictMatch) {
    return page.locator('.predict-match-input').filter({ hasText: match.homeTeamName })
  }

  async function submitPredictionViaUi(
    page: Page,
    match: SeedGuessablePredictMatch,
    homeGoals: number,
    awayGoals: number,
  ): Promise<void> {
    const container = matchContainer(page, match)
    await container.getByLabel(`${match.homeTeamName} predicted goals`).fill(String(homeGoals))
    await container.getByLabel(`${match.awayTeamName} predicted goals`).fill(String(awayGoals))
    await container.getByRole('button', { name: 'Save' }).click()
    await expect(container.getByText('Saved.')).toBeVisible()
  }

  // REQ-1301 (slate)/REQ-1302 (submission)/REQ-1306 (confirm-and-lock): one
  // continuous playthrough of an open round — view the 5-match slate,
  // submit a single prediction, confirm the confirm-button only appears
  // once all 5 are filled, cancel once (predictions stay editable), then
  // confirm for real and see the per-player lock notice/disabled fields.
  test('REQ-1301/1302/1306: view the 5-match slate, submit predictions, cancel then confirm-and-lock', async ({
    page,
    request,
  }) => {
    const seed = await seedPredictRound(request)
    expect(seed.matches).toHaveLength(5)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-predict-${tag}@test.invalid`
    const playerDisplayName = `Predict Player ${tag}`

    await signUpNewPredictPlayer(page, playerDisplayName, playerEmail)

    // ---- Slate (REQ-1301) ----------------------------------------------
    await expect(page.getByText('Predict the final score of all 5 matches.')).toBeVisible()
    await expect(page.locator('.predict-match-input')).toHaveCount(5)
    for (const match of seed.matches) {
      await expect(matchContainer(page, match)).toBeVisible()
    }

    // Neither lock notice applies yet, and the confirm button isn't offered
    // until every match has a stored prediction.
    await expect(page.getByTestId('predict-round-locked-notice')).not.toBeVisible()
    await expect(page.getByTestId('predict-confirmed-locked-notice')).not.toBeVisible()
    await expect(page.getByRole('button', { name: 'Confirm and lock my predictions' })).not.toBeVisible()

    // ---- Submission (REQ-1302) -----------------------------------------
    const [firstMatch, ...remainingMatches] = seed.matches
    await submitPredictionViaUi(page, firstMatch, 2, 1)

    // Still not all 5 filled — confirm button stays hidden.
    await expect(page.getByRole('button', { name: 'Confirm and lock my predictions' })).not.toBeVisible()

    for (const match of remainingMatches) {
      await submitPredictionViaUi(page, match, 1, 1)
    }

    // ---- Confirm-and-lock (REQ-1306) ------------------------------------
    const confirmButton = page.getByRole('button', { name: 'Confirm and lock my predictions' })
    await expect(confirmButton).toBeVisible()
    await confirmButton.click()

    const dialog = page.getByTestId('predict-confirm-dialog')
    await expect(dialog).toBeVisible()
    await expect(dialog).toContainText('Confirm and lock your predictions?')

    // Cancelling leaves every prediction exactly as freely editable as
    // before (REQ-1306's own "dismisses or cancels" acceptance criterion).
    await page.getByTestId('predict-confirm-dialog-cancel').click()
    await expect(dialog).not.toBeVisible()
    await expect(matchContainer(page, firstMatch).getByLabel(`${firstMatch.homeTeamName} predicted goals`)).toBeEnabled()
    await expect(page.getByTestId('predict-confirmed-locked-notice')).not.toBeVisible()

    // Confirming for real locks this player's predictions specifically.
    await confirmButton.click()
    await expect(dialog).toBeVisible()
    await page.getByTestId('predict-confirm-dialog-confirm').click()

    const confirmedNotice = page.getByTestId('predict-confirmed-locked-notice')
    await expect(confirmedNotice).toBeVisible()
    await expect(confirmedNotice).toContainText('confirmed and locked your predictions for this round')
    // REQ-1306 is independent of REQ-1303's round-wide lock — this round's
    // own automatic lock (first match's kickoff) hasn't happened yet.
    await expect(page.getByTestId('predict-round-locked-notice')).not.toBeVisible()

    await expect(matchContainer(page, firstMatch).getByLabel(`${firstMatch.homeTeamName} predicted goals`)).toBeDisabled()
    await expect(confirmButton).not.toBeVisible()
  })

  // REQ-1303: a round whose earliest match has already kicked off is locked
  // round-wide, for every player, independent of REQ-1306's per-player
  // early lock — this player never confirms anything.
  test('REQ-1303: a round that has already locked shows the round-wide-lock notice and disables predictions', async ({
    page,
    request,
  }) => {
    const seed = await seedPredictRound(request, -5)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-predict-locked-${tag}@test.invalid`
    const playerDisplayName = `Predict Locked ${tag}`

    await signUpNewPredictPlayer(page, playerDisplayName, playerEmail)

    const lockedNotice = page.getByTestId('predict-round-locked-notice')
    await expect(lockedNotice).toBeVisible()
    await expect(lockedNotice).toContainText('This round has locked')
    // This player never confirmed — the two lock notices are independent.
    await expect(page.getByTestId('predict-confirmed-locked-notice')).not.toBeVisible()

    const firstMatch = seed.matches[0]
    await expect(matchContainer(page, firstMatch).getByLabel(`${firstMatch.homeTeamName} predicted goals`)).toBeDisabled()
    // allFilled can never become true here (every field is disabled), so the
    // confirm action is never offered on an already-locked round either.
    await expect(page.getByRole('button', { name: 'Confirm and lock my predictions' })).not.toBeVisible()
  })

  // REQ-1304/1305 (grading) / REQ-410 (game-scoped leaderboard), gated on
  // S-199 (already merged — see docs/CHANGELOG.md's 2026-08-31 entry wiring
  // PredictRoundScoreSource into LeaderboardService): a graded prediction's
  // points reach the xG Predict leaderboard tab.
  test('REQ-1304/1305/410: a graded prediction shows up on the xG Predict leaderboard', async ({
    page,
    request,
  }) => {
    const seed = await seedPredictRound(request)
    const targetMatch = seed.matches[0]

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-predict-lb-${tag}@test.invalid`
    const playerDisplayName = `Predict LB ${tag}`

    await signUpNewPredictPlayer(page, playerDisplayName, playerEmail)
    // Predicts a 2-1 home win for the target match — graded below with the
    // identical actual score, so all 3 of REQ-1304's components (outcome,
    // home-goals, away-goals) match: 3 * ScoringRules.PredictPointsPerComponent
    // (10) = 30 points.
    await submitPredictionViaUi(page, targetMatch, 2, 1)

    // Grades the match directly via the repository's own normal write path
    // (InternalRoundEndpoints.cs's grade-predict-match test-data endpoint),
    // bypassing IFootballDataClient entirely — there is no deterministic way
    // to make a real football-data.org fixture finish with a specific score
    // from an E2E run.
    const gradeResponse = await request.post(
      `${API_BASE_URL}/internal/test-data/grade-predict-match/${targetMatch.matchId}`,
      { data: { homeGoals: 2, awayGoals: 1 } },
    )
    expect(gradeResponse.ok(), `grade-predict-match failed: ${gradeResponse.status()}`).toBeTruthy()

    const closeResponse = await request.post(
      `${API_BASE_URL}/internal/test-data/force-close-round/${seed.roundId}`,
    )
    expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    previousRoundId = null

    await page.getByRole('button', { name: 'Leaderboard' }).click()
    await page.getByRole('tab', { name: 'xG Predict' }).click()

    // "Previous Rounds" -> a specific closed round's own locked leaderboard,
    // same reasoning play-path.spec.ts's own leaderboard section documents:
    // REQ-409's >=5-qualifying-rounds floor would hide a single-round player
    // from "All-time" entirely, proving nothing either way about this REQ.
    const closedRoundsResponsePromise = page.waitForResponse(
      (response) =>
        response.url().includes('/leagues/global/leaderboard/closed-rounds') &&
        response.url().includes('gameKey=xg-predict') &&
        response.request().method() === 'GET',
    )
    await page.getByRole('tab', { name: 'Previous Rounds' }).click()
    const closedRoundsResponse = await closedRoundsResponsePromise
    const closedRoundsBody = (await closedRoundsResponse.json()) as { rounds: Array<{ roundId: string }> }
    expect(closedRoundsBody.rounds[0]?.roundId).toBe(seed.roundId)

    await page.locator('.leaderboard-screen__round-list-button').first().click()

    const playerRow = page.getByRole('listitem').filter({ hasText: playerDisplayName })
    await expect(playerRow).toBeVisible()
    await expect(playerRow.getByText('30 pts')).toBeVisible()
    await expect(playerRow.getByText('you')).toBeVisible()
  })
})
