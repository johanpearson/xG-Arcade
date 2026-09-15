import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-245 (docs/backlog.md): the UI equivalent of
// AdminEndpointTests.REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess
// — proves REQ-509's suggestion-review-and-commit flow AND REQ-510's
// standalone search-and-add flow each make a real, externally observable
// difference (an incorrect guess flips to correct on a later attempt), not
// just that the admin UI shows a success message. Also exercises REQ-215's
// submission half (the suggestion REQ-509 reviews here has to come from
// somewhere real, not a seeded row) and REQ-501's own "manual override
// always wins" outcome, reached this time through REQ-509/510's write path
// rather than AdminScreen's older manual-override form.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// ADR-0011/ADR-0018: this spec's own seed endpoint, both guess submissions,
// and both admin-panel lookups below each pay a real, live Wikidata
// round-trip cost (observed 9-27s in CI, per ADR-0011) — every assertion
// that follows one of those calls waits up to this instead of Playwright's
// default 5s, mirroring play-grid.spec.ts's own WRONG_GUESS_TIMEOUT_MS and
// its reasoning (including the "Not a match." wait below, which shouldn't
// itself need the live-lookup path but keeps the same safety margin anyway,
// per that file's own note).
const LIVE_LOOKUP_TIMEOUT_MS = 20_000

// REQ-509/510: `AdminAuthorizationHandler` (backend/src/XGArcade.Api/Auth/
// AdminAuthorization.cs) grants admin based on the JWT `sub` appearing in
// `Admin__UserIds` — ci.yml sets that to the fixed deterministic-from-email
// id `LocalE2EAuth.cs` (Auth:Mode=local-e2e) derives for exactly this
// email, so it must stay a fixed literal rather than this file's usual
// per-run-unique @test.invalid tag.
const ADMIN_EMAIL = 'e2e-admin@test.invalid'
const ADMIN_PASSWORD = 'password123'
const ADMIN_DISPLAY_NAME = 'E2E Admin'

// Matches SeedGuessableRoundWithMissingClubResponse
// (backend/src/XGArcade.Api/Rounds/InternalRoundEndpoints.cs) exactly
// (System.Text.Json's default camelCase policy).
interface SeedGuessableRoundWithMissingClubResponse {
  roundId: string
  cellId: string
  playerId: string
  wikidataQid: string
  correctPlayerFullName: string
  nationality: string
  expectedClubName: string
  allKnownClubs: string[]
}

// One continuous playthrough across two admin sub-flows and three
// authenticated identities (one regular player per seeded scenario, plus
// the admin) — serial mode and a generous timeout, same "protect against
// flakiness at near-zero cost" reasoning as play-grid.spec.ts/
// play-connect.spec.ts's own test.describe.configure calls. This test makes
// four real Wikidata round trips in total (two guess submissions that miss
// cache, two admin PlayerReviewPanel lookups) — sized well above
// 4 * LIVE_LOOKUP_TIMEOUT_MS plus normal UI overhead.
test.describe.configure({ mode: 'serial', timeout: 180_000 })

test.describe('REQ-215/501/509/510: admin review flows', () => {
  // Same "sweep away any pre-existing Active round before the first seed
  // call" defense-in-depth play-grid.spec.ts's own clearAnyExistingActiveRound
  // establishes (for state left over from a prior local run against a
  // long-lived dev DB) — duplicated here rather than imported since no
  // shared helper module exists yet for this suite's small set of E2E specs
  // (see that file's own doc comment for the full reasoning, unchanged
  // here). ci.yml's fresh per-run Postgres container never needs this, but
  // a local rerun against an already-migrated DB can.
  async function clearAnyExistingActiveRound(request: APIRequestContext): Promise<void> {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-admin-review-probe-${tag}@test.invalid`
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

  // Unlike every other spec in this suite, this test deliberately seeds TWO
  // co-existing Active xG Grid rounds (Vieira's/Bergkamp's — see the
  // sequencing comments in the test body for why that's useful here) rather
  // than closing one before seeding the next. GET /rounds/current resolves
  // a single "active round" per game key across the WHOLE shared CI
  // Postgres instance, not per-spec — so unlike play-grid.spec.ts/
  // play-path.spec.ts/etc., which each fold their own force-close into their
  // last test's own body (safe there, since they only ever have one round
  // outstanding at a time), a round left dangling here leaks into whatever
  // spec runs next in the same job and breaks its own "no active round"
  // assumption (confirmed by a real CI run: header-nav.spec.ts's REQ-720
  // empty-state case failed this exact way before this fix). Captured at
  // module scope by the test body below and closed here instead, in
  // test.afterAll, so cleanup runs regardless of which assertion in the
  // test body throws (or whether it throws at all) — never just
  // end-of-test-body code a failure could skip.
  let vieiraRoundId: string | undefined
  let bergkampRoundId: string | undefined

  test.afterAll(async ({ request }) => {
    for (const roundId of [vieiraRoundId, bergkampRoundId]) {
      if (!roundId) continue
      // Best-effort: this test's own assertions already report the real
      // failure if something went wrong above — a second, unrelated
      // cleanup failure on top would only obscure that, not add signal.
      await request.post(`${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`).catch(() => {})
    }
  })

  // REQ-807's third seed endpoint (S-245 addition): unlike seed-guessable-round,
  // this creates its OWN brand-new Round every call, entirely unrelated to
  // whichever round GET /rounds/current currently resolves as "the" active
  // one for the whole xG Grid game key (REQ-303) — see this file's own
  // sequencing comments in the test body below for why that's actually
  // useful here (two co-existing scenarios) rather than a hazard to force-
  // close around, unlike play-grid.spec.ts's own seedFreshRound.
  async function seedMissingClubRound(
    request: APIRequestContext,
    realPlayerName: string,
  ): Promise<SeedGuessableRoundWithMissingClubResponse> {
    const response = await request.post(
      `${API_BASE_URL}/internal/test-data/seed-guessable-round-with-missing-club?realPlayerName=${encodeURIComponent(realPlayerName)}`,
    )
    // Deliberately checked before touching the body (rather than the usual
    // `expect(response.ok(), ...).toBeTruthy()` one-liner this suite's
    // sibling specs use elsewhere) — response.text() consumes the body, and
    // a passing response still needs that same body read via .json() below,
    // so the two can't both unconditionally run.
    if (!response.ok()) {
      throw new Error(
        `seed-guessable-round-with-missing-club(${realPlayerName}) failed: ${response.status()} ${await response.text()}`,
      )
    }
    return response.json()
  }

  // REQ-701/REQ-806's @test.invalid convention (see play-grid.spec.ts's own
  // signUpNewPlayer) — a fresh, unique account per identity, real signup
  // endpoint, auto-logged-in, landing on "Choose a game" then straight into
  // xG Grid (Tier 0's only game with a suggestion entry point, REQ-215).
  async function signUpNewGridPlayer(page: Page): Promise<void> {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-admin-review-${tag}@test.invalid`

    // REQ-717/ADR-0037 follow-up: handleSubmit calls the real
    // getTurnstileToken() unconditionally -- stub window.turnstile before
    // this signup form ever submits (see turnstile-stub.ts).
    await stubTurnstile(page)
    await page.goto('/')
    // REQ-719: a fresh, unauthenticated visit lands on the splash screen
    // first, not AuthScreen directly.
    await page.getByRole('button', { name: 'Log in or sign up' }).click()
    await page.getByRole('tab', { name: 'Sign up' }).click()
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password', { exact: true }).fill('password123')
    await page.getByLabel('Confirm password').fill('password123')
    await page.getByLabel('Display name').fill(`Admin Review ${tag}`)
    await page.getByLabel(/at least 16 years old/).check()
    await page.getByRole('button', { name: 'Create account' }).click()

    await expect(page.getByText('Choose a game')).toBeVisible()
    await page.getByRole('button', { name: 'xG Grid' }).click()
  }

  // Signs up (ignoring the result) then always logs in through the real UI
  // form — resilient the way the story's own instructions call for: since
  // ADMIN_EMAIL is a fixed literal (unlike every other identity in this
  // suite), a local rerun against a persisted dev DB 409s on the signup
  // call (REQ-701's uniqueness check), but the account already exists by
  // then either way, so the UI login below always succeeds regardless of
  // which branch created it.
  async function loginAsAdmin(page: Page, request: APIRequestContext): Promise<void> {
    await request.post(`${API_BASE_URL}/auth/signup`, {
      data: {
        email: ADMIN_EMAIL,
        password: ADMIN_PASSWORD,
        confirmPassword: ADMIN_PASSWORD,
        displayName: ADMIN_DISPLAY_NAME,
        ageConfirmed: true,
        captchaToken: 'e2e-test-token',
      },
    })

    await stubTurnstile(page)
    await page.goto('/')
    await page.getByRole('button', { name: 'Log in or sign up' }).click()
    // AuthScreen's default tab is already "Log in" (Mode = 'login') — no
    // tab click needed, unlike signUpNewGridPlayer's Sign-up flow above.
    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Password').fill(ADMIN_PASSWORD)
    await page.getByRole('button', { name: 'Log in' }).click()
    await expect(page.getByText('Choose a game')).toBeVisible()
  }

  test('REQ-215/501/509/510: a suggestion review-commit and a standalone search-commit each flip a real guess from incorrect to correct', async ({
    page,
    request,
    browser,
  }) => {
    // ---- Scenario 1 setup (REQ-509: suggestion review-and-commit) --------
    // Patrick Vieira — a real, well-known, retired footballer (backend-
    // implementer's pick, S-245) whose nationality is already seeded as an
    // effective attribute; expectedClubName is deliberately not yet
    // effective, so a guess of correctPlayerFullName is guaranteed incorrect
    // until an admin commits it.
    const vieiraSeed = await seedMissingClubRound(request, 'Patrick Vieira')
    // Captured immediately (module-scope, read by test.afterAll above) —
    // even an assertion further down this test failing must not skip
    // closing this round, since it now exists in the shared CI DB either way.
    vieiraRoundId = vieiraSeed.roundId
    const vieiraCell = page.getByTestId(`grid-cell-${vieiraSeed.cellId}`)

    await signUpNewGridPlayer(page)

    // REQ-303: the seeded cell's categories render as headers/accessible
    // name — same "Guess {row} × {col}" shape play-grid.spec.ts already
    // pins, just with this endpoint's own real nationality/club values.
    await expect(vieiraCell).toHaveAccessibleName(`Guess ${vieiraSeed.nationality} × ${vieiraSeed.expectedClubName}`)

    await vieiraCell.click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await page.getByLabel('Player name').fill(vieiraSeed.correctPlayerFullName)
    await page.getByRole('button', { name: 'Submit guess' }).click()

    // REQ-509/510's whole premise: this exact name is guaranteed incorrect
    // before any admin action (the seed endpoint deliberately leaves
    // expectedClubName unsatisfied) — REQ-215's outcome view stays open with
    // the suggestion entry point, exactly as play-grid.spec.ts's own
    // intentionally-wrong-guess assertion does.
    await expect(page.getByText('Not a match.')).toBeVisible({ timeout: LIVE_LOOKUP_TIMEOUT_MS })
    await expect(page.getByTestId('suggestion-entry-point')).toBeVisible()

    // REQ-215: submit a suggestion for this exact incorrect guess — this is
    // the real, player-submitted input REQ-509's review queue is for, not a
    // seeded row.
    await page.getByTestId('suggestion-entry-point').click()
    await page.getByLabel('Club(s)').fill(vieiraSeed.expectedClubName)
    await page.getByLabel('Nationality').fill(vieiraSeed.nationality)
    await page.getByTestId('suggestion-submit').click()
    await expect(page.getByTestId('suggestion-confirmation')).toBeVisible()

    // Deliberately NOT closing/navigating away from this page/dialog here —
    // REQ-215's outcome view (with "Try another guess" still available,
    // since this cell isn't locked yet) is reused below, after the admin
    // acts, to prove the flip via the same still-open sheet.

    // ---- Scenario 2 setup (REQ-510: standalone search-and-add) -----------
    // Dennis Bergkamp — a second, independent real player/round/cell (no
    // suggestion involved for this one, per REQ-510's own "no suggestion
    // record required or created" clause). Seeded now (after Scenario 1's
    // guess/suggestion above), which is what makes it the new
    // most-recently-created Active round for GET /rounds/current
    // (REQ-303's OrderByDescending(StartTime) resolution) — this doesn't
    // disturb the page above at all, since GridScreen only ever fetches
    // /rounds/current once, on mount (lib/useRoundFetch.ts), and every
    // guess submission from here on addresses its own already-known
    // roundId/cellId directly rather than re-resolving "the" current round.
    const bergkampSeed = await seedMissingClubRound(request, 'Dennis Bergkamp')
    // Same "capture immediately, close in afterAll regardless of what
    // happens next" reasoning as vieiraRoundId above.
    bergkampRoundId = bergkampSeed.roundId

    const contextB = await browser.newContext()
    const contextC = await browser.newContext()
    try {
      // Context C: a second, independent player identity — mounts
      // GridScreen only now, once Bergkamp's round is genuinely the current
      // one, so it lands on that cell without needing to force-close
      // Vieira's still-open round first.
      const pageC = await contextC.newPage()
      await signUpNewGridPlayer(pageC)
      const bergkampCell = pageC.getByTestId(`grid-cell-${bergkampSeed.cellId}`)
      await expect(bergkampCell).toHaveAccessibleName(
        `Guess ${bergkampSeed.nationality} × ${bergkampSeed.expectedClubName}`,
      )

      await bergkampCell.click()
      await expect(pageC.getByRole('dialog')).toBeVisible()
      await pageC.getByLabel('Player name').fill(bergkampSeed.correctPlayerFullName)
      await pageC.getByRole('button', { name: 'Submit guess' }).click()
      // Same "guaranteed incorrect before any admin action" guarantee as
      // Scenario 1 — proves REQ-510's flip has something real to flip too,
      // not just REQ-509's.
      await expect(pageC.getByText('Not a match.')).toBeVisible({ timeout: LIVE_LOOKUP_TIMEOUT_MS })

      // ---- Admin review/commit (context B) — both scenarios -------------
      const pageB = await contextB.newPage()
      await loginAsAdmin(pageB, request)
      await pageB.getByRole('button', { name: 'Settings' }).click()
      // Only rendered when GET /auth/me's isAdmin is true
      // (settings-screen__admin-link, SettingsScreen.tsx) — asserting on it
      // being reachable is this suite's own proof the Admin__UserIds wiring
      // (ci.yml) actually took effect for this account, not just an
      // assumption.
      await pageB.getByRole('button', { name: 'Admin' }).click()
      await pageB.getByRole('tab', { name: 'Grid' }).click()
      // REQ-512's optional pending-count suffix ("Player suggestions (1)")
      // makes an exact string match brittle — tolerate it via regex, per
      // this story's own instructions.
      await pageB.getByRole('button', { name: /Player suggestions/ }).click()

      // REQ-509 half: find the row for the suggestion just submitted above
      // and review-and-commit it.
      const vieiraRow = pageB.getByRole('listitem').filter({ hasText: vieiraSeed.correctPlayerFullName })
      await expect(vieiraRow).toBeVisible()
      await vieiraRow.getByRole('button', { name: 'Review' }).click()

      // PlayerReviewPanel runs a real, live Wikidata lookup on open
      // ("Looking up player on Wikidata…") — wait for it to resolve with
      // the same generous timeout as every other live round trip in this
      // file. Once resolved, Full name/Nationality/Clubs are pre-filled
      // from that live response; left as-is here (committing the full
      // clubs list is fine and simplest — expectedClubName is necessarily
      // among them, and every other club is either new or already
      // effective, both harmless per the endpoint's own doc comment).
      await expect(vieiraRow.getByRole('button', { name: 'Commit' })).toBeVisible({ timeout: LIVE_LOOKUP_TIMEOUT_MS })
      // Nationality comes back non-blank for a real footballer, which makes
      // Reason required (PlayerReviewPanel's canCommit/hasNationalityText
      // logic, ADR-0060) — any non-empty string satisfies it.
      await vieiraRow.getByLabel('Reason').fill('Confirmed via Wikidata (S-245 E2E).')
      await vieiraRow.getByRole('button', { name: 'Commit' }).click()

      // S-129: describeCommitResult's exact wording is dynamic/unpinned
      // here on purpose — only that SOME non-empty confirmation appears,
      // scoped to the pending-suggestions section specifically (not the
      // Search-Wikidata-directly section below, which renders its own,
      // separate confirmation element with the same CSS class once
      // Scenario 2's commit happens further down).
      const pendingSection = pageB.locator('.suggestions-screen__section').filter({ hasText: 'Pending suggestions' })
      const pendingConfirmation = pendingSection.locator('.suggestions-screen__confirmation')
      await expect(pendingConfirmation).toBeVisible()
      await expect(pendingConfirmation).not.toBeEmpty()

      // REQ-510 half: standalone search-and-add for Bergkamp, independent
      // of any suggestion record (none was ever submitted for this one).
      const searchSection = pageB.locator('.suggestions-screen__section').filter({ hasText: 'Search Wikidata directly' })
      await searchSection.getByLabel('Player name').fill(bergkampSeed.correctPlayerFullName)
      await searchSection.getByRole('button', { name: 'Search' }).click()

      // Same shared PlayerReviewPanel/live-lookup/pre-fill behavior as the
      // REQ-509 half above — see that block's own comments for the full
      // reasoning, not repeated here (ADR-0053: "a variant entry point...
      // not a parallel reimplementation").
      await expect(searchSection.getByRole('button', { name: 'Commit' })).toBeVisible({ timeout: LIVE_LOOKUP_TIMEOUT_MS })
      await searchSection.getByLabel('Reason').fill('Confirmed via Wikidata (S-245 E2E).')
      await searchSection.getByRole('button', { name: 'Commit' }).click()

      const searchConfirmation = searchSection.locator('.suggestions-screen__confirmation')
      await expect(searchConfirmation).toBeVisible()
      await expect(searchConfirmation).not.toBeEmpty()

      // ---- REQ-510 verification: Bergkamp's guess now flips to correct --
      // Continues within context C's own still-open outcome view (not
      // locked — only 1 of 2 attempts used), same "Try another guess"
      // pattern REQ-215's UI offers rather than reopening the cell.
      await pageC.getByRole('button', { name: 'Try another guess' }).click()
      await pageC.getByLabel('Player name').fill(bergkampSeed.correctPlayerFullName)
      await pageC.getByRole('button', { name: 'Submit guess' }).click()
      // REQ-210: a correct answer locks the cell immediately and closes the
      // sheet — the same "flips from incorrect to correct" outcome
      // REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess
      // proves at the API level, now proven through the real UI/endpoints
      // for REQ-510's commit path specifically.
      await expect(pageC.getByRole('dialog')).not.toBeVisible({ timeout: LIVE_LOOKUP_TIMEOUT_MS })
    } finally {
      await contextB.close()
      await contextC.close()
    }

    // ---- REQ-509 verification: Vieira's guess now flips to correct -------
    // Back on context A's original page/dialog, untouched since the
    // suggestion was submitted above.
    await page.getByRole('button', { name: 'Try another guess' }).click()
    await page.getByLabel('Player name').fill(vieiraSeed.correctPlayerFullName)
    await page.getByRole('button', { name: 'Submit guess' }).click()
    // Same REQ-210 lock-and-close outcome as the REQ-510 verification above
    // — this is the REQ-509 half of this file's own "mirrors
    // REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess"
    // acceptance criterion.
    await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: LIVE_LOOKUP_TIMEOUT_MS })
  })
})
