import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-229: this file's structure deliberately mirrors play-path.spec.ts's own
// conventions throughout (API_BASE_URL, stubTurnstile usage, serial mode,
// clearAnyExisting*/seed*/signUp* helper shapes, @test.invalid emails,
// closed-round leaderboard drill-in) — see that file's own comments for the
// parts of the reasoning that are identical for xG Higher/Lower and aren't
// repeated here. xG Higher/Lower is (ADR-0110) a single-attempt-per-round
// streak game like xG Path, not a whole-slate game like xG Grid/xG Predict,
// so play-path.spec.ts is the closer structural precedent of the two.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// Matches backend/src/XGArcade.Api/Rounds/InternalRoundEndpoints.cs's
// SeedGuessableHigherLowerRoundResponse/SeedGuessableHigherLowerComparatorResponse
// records exactly (System.Text.Json's default camelCase policy). Comparators
// is in fixed sequence order (position 0 first) and — unlike the real GET
// /higher-lower/current response, which deliberately withholds a next
// comparator's Value per REQ-1504 — carries every position's real Value
// directly, since this E2E suite needs to know each position's value up
// front to choose the correct Higher/Lower direction deterministically.
interface SeedGuessableHigherLowerComparator {
  playerId: string
  name: string
  value: number
}
interface SeedGuessableHigherLowerRoundResponse {
  roundId: string
  statCategory: string
  baselinePlayerName: string
  baselineValue: number
  comparators: SeedGuessableHigherLowerComparator[]
}

// Unlike xG Grid's guesses, an xG Higher/Lower guess never pays ADR-0018's
// live-Wikidata-lookup cost — HigherLowerEndpoints.cs's write path
// (XGHigherLowerGameModule.ScoreSubmissionAsync) only ever compares two
// already-persisted integers. Same "no equivalent of
// WRONG_GUESS_TIMEOUT_MS" reasoning play-path.spec.ts's own comment already
// documents; default Playwright expect/test timeouts are used throughout.

// GET /higher-lower/current resolves "the" currently Active round for the
// whole xg-higher-lower GameKey the same no-per-caller-scoping way REQ-303's
// GET /rounds/current and REQ-1203's GET /path/current do (see those files'
// own comments) — so this file needs its own "clear a leftover Active
// round" defense scoped to GET /higher-lower/current specifically, and runs
// serially for the same defense-in-depth reasoning.
test.describe.configure({ mode: 'serial', timeout: 60_000 })

test.describe('REQ-1501/1502/1503/1504/1505/1210: play a full xG Higher/Lower round', () => {
  let previousRoundId: string | null = null

  // Repeatedly closes whatever round GET /higher-lower/current currently
  // reports as Active until none remains — same "there's no
  // list-active-rounds endpoint" limitation play-grid.spec.ts's/
  // play-path.spec.ts's own equivalent helpers document. A throwaway probe
  // account is used purely to read that endpoint, never to submit guesses.
  async function clearAnyExistingActiveHigherLowerRound(request: APIRequestContext): Promise<void> {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-hl-probe-${tag}@test.invalid`
    await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', confirmPassword: 'password123', displayName: `HL Probe ${tag}`, ageConfirmed: true, captchaToken: 'e2e-test-token' },
    })
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `probe login failed: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }

    for (let attempt = 0; attempt < 10; attempt += 1) {
      const roundResponse = await request.get(`${API_BASE_URL}/higher-lower/current`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      })
      if (roundResponse.status() === 404) return
      expect(roundResponse.ok(), `GET /higher-lower/current failed: ${roundResponse.status()}`).toBeTruthy()
      const { roundId } = (await roundResponse.json()) as { roundId: string }
      // REQ-806's boundary: force-close-round/{roundId} is game-agnostic
      // (IRoundCloseService.CloseRoundAsync just closes whatever round id
      // it's given) — the same endpoint play-grid.spec.ts/play-path.spec.ts
      // already rely on works unchanged for an xg-higher-lower round.
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }
    throw new Error('clearAnyExistingActiveHigherLowerRound: too many pre-existing Active rounds to clear.')
  }

  test.beforeAll(async ({ request }) => {
    await clearAnyExistingActiveHigherLowerRound(request)
  })

  // Closes the previous test's round (if any) before seeding a fresh one —
  // same single-shared-previousRoundId convention play-predict.spec.ts's own
  // seedPredictRound uses, so at most one xg-higher-lower round is ever
  // Active at once across this file's serial tests. comparatorCount controls
  // the seeded sequence length directly (see InternalRoundEndpoints.cs's own
  // doc comment on this endpoint) — a small value (1) is used by the
  // full-completion/REQ-1210 test below to reach the Round's full-length
  // terminal case in exactly one correct guess.
  async function seedHigherLowerRound(
    request: APIRequestContext,
    comparatorCount?: number,
  ): Promise<SeedGuessableHigherLowerRoundResponse> {
    if (previousRoundId) {
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${previousRoundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }

    const url = comparatorCount === undefined
      ? `${API_BASE_URL}/internal/test-data/seed-guessable-higher-lower-round`
      : `${API_BASE_URL}/internal/test-data/seed-guessable-higher-lower-round?comparatorCount=${comparatorCount}`
    const response = await request.post(url)
    expect(response.ok(), `seed-guessable-higher-lower-round failed: ${response.status()}`).toBeTruthy()
    const body = (await response.json()) as SeedGuessableHigherLowerRoundResponse
    previousRoundId = body.roundId
    return body
  }

  // REQ-701/REQ-806's real-signup-endpoint convention (same as
  // play-path.spec.ts's signUpNewPathPlayer): a fresh, unique @test.invalid
  // account, created and auto-logged-in through the real UI. Ends on
  // GameSelectScreen's "xG Higher/Lower" tile (SCREEN-09,
  // frontend/src/games/GameSelectScreen.tsx's aria-label="xG Higher/Lower"
  // button) and clicks through to SCREEN-18.
  async function signUpNewHigherLowerPlayer(page: Page, displayName: string, email: string): Promise<void> {
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

    await page.getByRole('button', { name: 'xG Higher/Lower' }).click()
  }

  // REQ-1501/1502/1503: the Round's fixed category, starting baseline, and
  // first comparator are all shown correctly on load, and the next
  // comparator's value stays hidden until guessed (REQ-1504's own "identity
  // only" contract, enforced at the DTO shape level — HigherLowerScreen.tsx
  // has no Value field to render even if it wanted to).
  test('REQ-1501/1502/1503: loads the active round with the correct baseline value and comparator category', async ({
    page,
    request,
  }) => {
    const seed = await seedHigherLowerRound(request, 3)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-hl-load-${tag}@test.invalid`
    const playerDisplayName = `HL Load ${tag}`

    await signUpNewHigherLowerPlayer(page, playerDisplayName, playerEmail)

    await expect(page.getByRole('heading', { name: 'xG Higher/Lower' })).toBeVisible()
    await expect(page.getByText(`Comparing ${seed.statCategory}`)).toBeVisible()
    await expect(page.getByText('Streak 0 of 3')).toBeVisible()

    const baselineCard = page.locator('.higher-lower-screen__card--baseline')
    await expect(baselineCard.getByText(seed.baselinePlayerName)).toBeVisible()
    await expect(baselineCard.locator('.higher-lower-screen__player-value')).toHaveText(String(seed.baselineValue))

    const firstComparator = seed.comparators[0]
    const nextCard = page.locator('.higher-lower-screen__card--next')
    await expect(nextCard.getByText(firstComparator.name)).toBeVisible()
    // REQ-1504: identity only — the actual value is never rendered anywhere
    // on this card until it's guessed.
    await expect(nextCard).not.toContainText(String(firstComparator.value))

    await expect(page.getByRole('button', { name: 'Higher' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Lower' })).toBeVisible()
  })

  // REQ-1502/1504: a correct guess reveals the just-guessed comparator's
  // real value, increments the streak, replaces the baseline with it, and
  // loads the next comparator already fixed in the Round's sequence.
  test('REQ-1502/1504: a correct guess reveals the value, advances the streak, and loads the next comparator', async ({
    page,
    request,
  }) => {
    const seed = await seedHigherLowerRound(request, 3)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-hl-correct-${tag}@test.invalid`
    const playerDisplayName = `HL Correct ${tag}`

    await signUpNewHigherLowerPlayer(page, playerDisplayName, playerEmail)
    await expect(page.getByText('Streak 0 of 3')).toBeVisible()

    // The seeded sequence is strictly increasing from the baseline (100,
    // then +10 per position — see seed-guessable-higher-lower-round's own
    // doc comment in InternalRoundEndpoints.cs), so "Higher" is always the
    // correct answer for the first comparator against the seeded baseline.
    const guessedComparator = seed.comparators[0]
    await page.getByRole('button', { name: 'Higher' }).click()

    const outcome = page.locator('.higher-lower-screen__outcome--correct')
    await expect(outcome).toBeVisible()
    await expect(outcome).toContainText('Correct.')
    await expect(outcome).toContainText(guessedComparator.name)
    await expect(outcome).toContainText(String(guessedComparator.value))

    await expect(page.getByText('Streak 1 of 3')).toBeVisible()

    // The just-guessed comparator becomes the new baseline (value revealed).
    const baselineCard = page.locator('.higher-lower-screen__card--baseline')
    await expect(baselineCard.getByText(guessedComparator.name)).toBeVisible()
    await expect(baselineCard.locator('.higher-lower-screen__player-value')).toHaveText(String(guessedComparator.value))

    // The next comparator already fixed in the Round's sequence (REQ-1502/
    // 1503) is shown next, identity only.
    const secondComparator = seed.comparators[1]
    const nextCard = page.locator('.higher-lower-screen__card--next')
    await expect(nextCard.getByText(secondComparator.name)).toBeVisible()

    // The Round isn't complete yet (streak 1 of 3) — no completion banner.
    await expect(page.getByRole('status')).not.toBeVisible()
  })

  // REQ-1504: an incorrect guess reveals the actual value, ends the
  // attempt at the streak length reached BEFORE this guess (never
  // incremented), and offers no further guess.
  test('REQ-1504: an incorrect guess reveals the correct value and ends the attempt with no further guessing offered', async ({
    page,
    request,
  }) => {
    const seed = await seedHigherLowerRound(request, 3)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-hl-incorrect-${tag}@test.invalid`
    const playerDisplayName = `HL Incorrect ${tag}`

    await signUpNewHigherLowerPlayer(page, playerDisplayName, playerEmail)
    await expect(page.getByText('Streak 0 of 3')).toBeVisible()

    // The seeded sequence is strictly increasing from the baseline (see the
    // "correct guess" test above's own comment), so "Lower" is always the
    // wrong answer for the first comparator against the seeded baseline.
    const guessedComparator = seed.comparators[0]
    await page.getByRole('button', { name: 'Lower' }).click()

    const outcome = page.locator('.higher-lower-screen__outcome--incorrect')
    await expect(outcome).toBeVisible()
    await expect(outcome).toContainText('Incorrect.')
    await expect(outcome).toContainText(guessedComparator.name)
    await expect(outcome).toContainText(String(guessedComparator.value))

    // REQ-1504: the attempt ends at the streak length reached before this
    // guess — 0 here, since this was the very first guess of the attempt.
    await expect(page.getByText('Streak 0 of 3')).toBeVisible()
    await expect(page.getByText('You’ve completed this round.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Higher' })).not.toBeVisible()
    await expect(page.getByRole('button', { name: 'Lower' })).not.toBeVisible()

    // An incorrect guess never advances the baseline (XGHigherLowerGameModule
    // .ScoreSubmissionAsync's own "the baseline does not advance" comment) —
    // the card shown is still the Round's original seeded baseline.
    const baselineCard = page.locator('.higher-lower-screen__card--baseline')
    await expect(baselineCard.getByText(seed.baselinePlayerName)).toBeVisible()
  })

  // REQ-1504's full-length terminal case (every comparator in the Round's
  // fixed sequence guessed correctly) / REQ-1210/ADR-0083's round-completion
  // banner (this game's own diverging-from-xG-Predict status note, SCREEN-18)
  // / REQ-1505 (streak length recorded as FinalPoints, reachable through the
  // closed-round leaderboard read path).
  test('REQ-1504/1505/1210: completing the round shows the completion banner, and its link leads to this player\'s leaderboard row', async ({
    page,
    request,
  }) => {
    // comparatorCount=1: a single correct guess both advances the streak AND
    // reaches the Round's full configured length in the same guess —
    // REQ-1504's own full-length terminal case.
    const seed = await seedHigherLowerRound(request, 1)

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-hl-complete-${tag}@test.invalid`
    const playerDisplayName = `HL Complete ${tag}`

    await signUpNewHigherLowerPlayer(page, playerDisplayName, playerEmail)
    await expect(page.getByText('Streak 0 of 1')).toBeVisible()

    await page.getByRole('button', { name: 'Higher' }).click()

    await expect(page.getByText('Streak 1 of 1')).toBeVisible()
    await expect(page.getByText('You’ve completed this round.')).toBeVisible()

    // REQ-1210: the banner (role="status") shows immediately, with plain
    // "N pts" — a streak's FinalPoints is exactly the streak length reached,
    // never a provisional "~estimated" value (that's xG Grid's own wording,
    // not this game's — see HigherLowerScreen.tsx's own comment).
    const banner = page.getByRole('status')
    await expect(banner).toBeVisible()
    await expect(banner).toContainText('Round complete')
    await expect(banner).toContainText('1 pts')
    await expect(banner).not.toContainText('estimated')

    // Close the round for real before following the banner's link, so
    // REQ-1210's live-vs-past resolution (checkRoundStillLive) lands on the
    // already-proven "past" branch — same reasoning play-path.spec.ts's/
    // play-predict.spec.ts's own "Previous Rounds" leaderboard sections use
    // (REQ-409's >=5-qualifying-rounds floor would hide a single-round
    // player from "All-time" entirely, proving nothing either way about this
    // REQ).
    const closeResponse = await request.post(
      `${API_BASE_URL}/internal/test-data/force-close-round/${seed.roundId}`,
    )
    expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    previousRoundId = null

    const closedRoundsResponsePromise = page.waitForResponse(
      (response) =>
        response.url().includes('/leagues/global/leaderboard/closed-rounds') &&
        response.url().includes('gameKey=xg-higher-lower') &&
        response.request().method() === 'GET',
    )
    // REQ-1210/ADR-0083: unlike play-path.spec.ts's/play-predict.spec.ts's
    // own "Previous Rounds" flows (a manual tab click with no seeded round,
    // landing on the round list — hence their own closedRoundsResponsePromise
    // being the only fetch in flight), this banner link seeds `initialRoundId`
    // straight through App.tsx's handleViewRoundLeaderboard. That makes
    // PastRoundsLeaderboard fire the round-list fetch AND the round-detail
    // fetch (fetchClosedRoundLeaderboard, GET
    // /leagues/global/leaderboard/closed-rounds/{roundId} —
    // frontend/src/lib/leaderboard.ts) concurrently on entry, and it
    // auto-drills into that round's detail (skipping the list UI entirely —
    // see PastRoundsLeaderboard.tsx's own `if (selectedRoundId &&
    // pastDetailState)` branch), so there is no
    // `.leaderboard-screen__round-list-button` to click on this path. Both
    // response promises are armed before the click that triggers them (not
    // after), same reasoning as closedRoundsResponsePromise itself: arming a
    // waitForResponse after its request may have already fired/resolved
    // risks missing the event and hanging until timeout.
    const roundDetailResponsePromise = page.waitForResponse(
      (response) =>
        response.url().includes(`/leaderboard/closed-rounds/${seed.roundId}`) &&
        response.request().method() === 'GET',
    )
    // Scoped to the banner (role="status") so this never risks matching
    // HeaderNav's separate "Leaderboard" entry point — same
    // strict-mode-avoidance discipline play-path.spec.ts's own click uses,
    // just via containment here instead of `exact: true` (this button's own
    // accessible name, "View leaderboard," never collides with HeaderNav's
    // "Leaderboard" either way).
    await banner.getByRole('button', { name: 'View leaderboard' }).click()

    const closedRoundsResponse = await closedRoundsResponsePromise
    const closedRoundsBody = (await closedRoundsResponse.json()) as { rounds: Array<{ roundId: string }> }
    expect(closedRoundsBody.rounds[0]?.roundId).toBe(seed.roundId)

    // Lands directly on the xG Higher/Lower tab + "Previous Rounds" scope
    // (REQ-1210/ADR-0083's leaderboardInitial seed, App.tsx's
    // handleViewRoundLeaderboard) — asserted via the tabs' own aria-selected
    // state, not assumed from the response above alone.
    await expect(page.getByRole('tab', { name: 'xG Higher/Lower' })).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('tab', { name: 'Previous Rounds' })).toHaveAttribute('aria-selected', 'true')

    // Wait for the auto-drilled-into round's own detail fetch to finish
    // loading before asserting on its rows — no list-button click on this
    // path (see the comment above).
    await roundDetailResponsePromise

    const playerRow = page.getByRole('listitem').filter({ hasText: playerDisplayName })
    await expect(playerRow).toBeVisible()
    // REQ-1505: FinalPoints = streak length reached = comparatorCount (1),
    // since the single seeded comparator was guessed correctly.
    await expect(playerRow.getByText('1 pts')).toBeVisible()
    // Text, not color-only (design-document.md §6) — LeaderboardRowsList's
    // own "you" tag for the requesting player's row.
    await expect(playerRow.getByText('you')).toBeVisible()
  })
})
