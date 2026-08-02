import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-088: this file's structure deliberately mirrors play-grid.spec.ts's own
// conventions throughout (API_BASE_URL, stubTurnstile usage, serial mode,
// clearAnyExisting*/seed*/signUp* helper shapes, @test.invalid emails) —
// see that file's own comments for the parts of the reasoning that are
// identical for xG Path and aren't repeated here. Comments below focus on
// what's different for xG Path.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// Matches backend/src/XGArcade.Api/Rounds/InternalRoundEndpoints.cs's
// SeedGuessablePathRoundResponse record exactly (System.Text.Json's default
// camelCase policy). PuzzleId IS the "cell id" the existing game-agnostic
// POST /rounds/{roundId}/cells/{cellId}/guesses endpoint expects — same
// PathPuzzle.Id-is-the-cell-id contract IGameModule.GetCellIdsAsync already
// documents for xg-path (see that endpoint's own doc comment).
interface SeedGuessablePathRoundResponse {
  roundId: string
  puzzleId: string
  correctPlayerName: string
}

// Unlike xG Grid's guesses, an xG Path guess never pays ADR-0018's live-
// Wikidata-lookup cost: XGPathGameModule.ScoreSubmissionAsync (backend/src/
// XGArcade.Games.XGPath/XGPathGameModule.cs) resolves correctness purely via
// Player.NormalizedFullName/PlayerAlias.NormalizedAlias lookups against
// already-persisted rows — it never calls RefreshCellFromLiveLookupAsync or
// anything Wikidata-shaped (that method exists only on GridGameModule, and
// GuessSubmissionService's own call to IGameModule.ScoreSubmissionAsync is
// game-agnostic, so nothing routes xg-path through it either way). Confirmed
// by reading both files directly, not assumed — so this suite has no
// equivalent of play-grid.spec.ts's WRONG_GUESS_TIMEOUT_MS; default
// Playwright expect/test timeouts are used throughout.

// REQ-1203's GET /path/current resolves "the" currently Active round for the
// whole xg-path GameKey the same way REQ-303's GET /rounds/current does for
// xg-grid (XGArcade.Api.Path.PathEndpoints.MapPathEndpoints calls
// roundRepository.GetActiveByGameKeyAsync(XGPathGameModule.XGPathGameKey, ...)
// — same repository method, same "no per-caller scoping" shape, same
// OrderByDescending(StartTime) fix play-grid.spec.ts's own comment
// describes). GET /rounds/current itself is scoped to xg-grid's GameKey only
// (RoundEndpoints.cs hardcodes GridGameModule.XGGridGameKey), so this file's
// own "clear a leftover Active round" defense has to probe GET /path/current
// specifically — clearing a stray xg-grid round via play-grid.spec.ts's own
// helper would do nothing for a leftover xg-path round, and vice versa; the
// two games' "only one Active round per GameKey" races are independent of
// each other. Run serially for the same defense-in-depth reasoning
// play-grid.spec.ts documents (this file happens to have only one test today,
// but serial mode costs nothing and protects any test added here later).
test.describe.configure({ mode: 'serial', timeout: 60_000 })

test.describe('REQ-1201/1202/1203/1204/1205/1206/410/408: play a full xG Path round', () => {
  // Repeatedly closes whatever round GET /path/current currently reports as
  // Active (there's no "list active rounds" endpoint, same limitation
  // play-grid.spec.ts's equivalent helper documents) until none remains. A
  // throwaway probe account is used purely to read that endpoint, never to
  // submit guesses — same REQ-701 unique-tag reasoning as play-grid.spec.ts's
  // own probe (a fixed "Probe" display name would collide with itself on a
  // second local run against a persisted dev DB).
  async function clearAnyExistingActivePathRound(request: APIRequestContext): Promise<void> {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-path-probe-${tag}@test.invalid`
    await request.post(`${API_BASE_URL}/auth/signup`, {
      data: { email, password: 'password123', confirmPassword: 'password123', displayName: `Path Probe ${tag}`, ageConfirmed: true, captchaToken: 'e2e-test-token' },
    })
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `probe login failed: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }

    for (let attempt = 0; attempt < 10; attempt += 1) {
      const roundResponse = await request.get(`${API_BASE_URL}/path/current`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      })
      if (roundResponse.status() === 404) return
      expect(roundResponse.ok(), `GET /path/current failed: ${roundResponse.status()}`).toBeTruthy()
      const { roundId } = (await roundResponse.json()) as { roundId: string }
      // REQ-806's boundary: force-close-round/{roundId} is game-agnostic
      // (IRoundCloseService.CloseRoundAsync just closes whatever round id
      // it's given) — the same endpoint play-grid.spec.ts already relies on
      // works unchanged for an xg-path round.
      const closeResponse = await request.post(
        `${API_BASE_URL}/internal/test-data/force-close-round/${roundId}`,
      )
      expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()
    }
    throw new Error('clearAnyExistingActivePathRound: too many pre-existing Active rounds to clear.')
  }

  test.beforeAll(async ({ request }) => {
    await clearAnyExistingActivePathRound(request)
  })

  // REQ-701/REQ-806's real-signup-endpoint convention (same as
  // play-grid.spec.ts's signUpNewPlayer): a fresh, unique @test.invalid
  // account, created and auto-logged-in through the real UI. Ends on
  // GameSelectScreen's "xG Path" tile (SCREEN-09,
  // frontend/src/games/GameSelectScreen.tsx's aria-label="xG Path" button —
  // distinct from HeaderNav's own, separately-reachable "xG Path" entry,
  // which lives inside a collapsed "Games" disclosure not opened here) and
  // clicks through to SCREEN-10.
  async function signUpNewPathPlayer(page: Page, displayName: string, email: string): Promise<void> {
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

    await page.getByRole('button', { name: 'xG Path' }).click()
  }

  // REQ-1201/1202 (generation) / REQ-1203 (clue reveal order) / REQ-1204
  // (correctness) / REQ-1205 (attempt-cap lock-on-correct path) / REQ-1206
  // (clue-efficiency scoring) / REQ-410+ADR-0043 (game-scoped leaderboard) /
  // REQ-408 (previous-rounds drill-in): one continuous playthrough of the
  // seeded puzzle, end to end, matching S-088's own accept criterion of one
  // full spec covering generation -> clue reveal -> guess -> round close ->
  // leaderboard.
  test('REQ-1201/1202/1203/1204/1205/1206/410/408: signup, clue reveal, wrong then correct guess, round close, game-scoped leaderboard', async ({
    page,
    request,
  }) => {
    // ---- Generation --------------------------------------------------
    // Mirrors seed-guessable-round's own precedent for xG Grid
    // (InternalRoundEndpoints.cs's own doc comment on the sibling
    // endpoint): this bypasses XGPathGameModule.GenerateInstanceAsync
    // entirely (writes PathInstance/PathPuzzle directly via
    // IPathInstanceRepository), so REQ-1201's seeded-club/appearance-count
    // eligibility rules never actually run here — this is "generation" only
    // in the sense that a real, playable Round + PathInstance + one
    // PathPuzzle now exists, not a proof that XGPathGameModule's own
    // eligibility-filtered generation path was exercised (that's
    // XGPathGameModuleTests's job, per REQ-1201/1202's own Unit test-level
    // notes). The seeded target has exactly 3 PlayerCareerStint rows (Ajax
    // 2010-13, Juventus 2013-16, Real Madrid 2016-19) and no Position/
    // nationality/BirthYear data, so REQ-1203's 3-way club split for N=3 is
    // 1-1-1 (PathClueSequenceBuilder.SplitIntoTurns) — turn 1 reveals only
    // Ajax, turn 2 only Juventus, turn 3 only Real Madrid.
    const seedResponse = await request.post(`${API_BASE_URL}/internal/test-data/seed-guessable-path-round`)
    expect(seedResponse.ok(), `seed-guessable-path-round failed: ${seedResponse.status()}`).toBeTruthy()
    const seed = (await seedResponse.json()) as SeedGuessablePathRoundResponse

    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const playerEmail = `test-path-${tag}@test.invalid`
    const playerDisplayName = `Path Player ${tag}`

    await signUpNewPathPlayer(page, playerDisplayName, playerEmail)

    // ---- Clue reveal (REQ-1203) ---------------------------------------
    // PathScreen.tsx's puzzle-position text: "Puzzle {n} of {puzzles.length}"
    // — the seed produces a round with exactly one puzzle, so this is
    // always "Puzzle 1 of 1" for this round, never "Next puzzle" once
    // solved (PathScreen.tsx's own isLastPuzzle branch, asserted below).
    await expect(page.getByText('Puzzle 1 of 1')).toBeVisible()
    // Before any guess, GET /path/current's guess is null (attemptCount=0,
    // isCorrect=false), so PathClueSequenceBuilder.GetRevealedTurnCount(0,
    // false) = min(0+1,7) = 1 revealed turn — the first of the 3 club-reveal
    // turns, Ajax (PathTimeline.tsx renders each club's name in a
    // .path-timeline__club-name span).
    await expect(page.getByText('Ajax')).toBeVisible()
    await expect(page.getByText('Juventus')).not.toBeVisible()
    // PathGuessInput.tsx's "Clue {clueCount} of {MAX_CLUES_PER_PUZZLE}"
    // counter, clueCount = puzzle.clues.length = 1 here.
    await expect(page.getByText('Clue 1 of 7')).toBeVisible()

    const nameField = page.getByLabel('Player name')
    // PathGuessInput.tsx's submit button is labeled "Guess" — deliberately
    // different from GuessInput.tsx's (xG Grid) "Submit guess", and this
    // screen has no modal dialog to open first (SCREEN-10 is a persistent
    // inline form, not GuessInput's cell-click sheet) — see PathGuessInput
    // .tsx's own top-of-file comment on why there's no REQ-209
    // disambiguation picker or REQ-215 suggestion entry point here.
    const guessButton = page.getByRole('button', { name: 'Guess' })

    // ---- Guess: an intentionally wrong attempt (REQ-1204/1205) --------
    await nameField.fill('Definitely Not A Real Path Player')
    await guessButton.click()

    // PathScreen.tsx's handleSubmitGuess re-fetches GET /path/current after
    // every submission (POST .../guesses carries no clue data of its own —
    // see that file's own comment) — the newly revealed 2nd turn (Juventus)
    // appearing is itself the observable proof this round-trip completed,
    // used below instead of an arbitrary wait.
    await expect(page.getByText('Juventus')).toBeVisible()
    // attemptCount is now 1, not yet correct ->
    // GetRevealedTurnCount(1, false) = min(1+1,7) = 2 revealed turns (Ajax +
    // Juventus) -> PathGuessInput's clueCount prop is 2.
    await expect(page.getByText('Clue 2 of 7')).toBeVisible()
    // design-document.md SCREEN-10 "Rejected guess": reuses SCREEN-02's
    // shake-and-flash cue verbatim (PathGuessInput.tsx's key={shakeToken}
    // remount technique, same as CellState.tsx's useShakeToken) — same
    // "presence of the trigger class only" scope as play-grid.spec.ts's own
    // equivalent assertion (PathGuessInput.test.tsx's constructed-props
    // tests cover the trigger logic itself; the CSS keyframes are a visual
    // concern neither suite drives directly).
    await expect(page.locator('.path-guess-input--shake')).toBeVisible()
    // PathGuessInput.tsx's handleSubmit clears the typed name on a rejected
    // guess (`if (!correct) { ...; setName(''); }`) so the player can retype
    // straight away — unlike xG Grid's GuessInput, there's no separate
    // "outcome view"/"Try another guess" step here; the same inline field
    // just becomes editable again immediately.
    await expect(nameField).toHaveValue('')
    await expect(nameField).toBeEnabled()
    await expect(guessButton).toBeEnabled()

    // ---- Guess: the correct player name (REQ-1204/1205) ---------------
    await nameField.fill(seed.correctPlayerName)
    await guessButton.click()

    // REQ-1203's "no further clue is ever revealed once solved" / REQ-1205's
    // "locks immediately regardless of remaining attempts": the winning
    // guess's own attemptCount (2, one wrong + this correct one) freezes
    // GetRevealedTurnCount(2, true) = min(2,7) = 2 -- so the timeline still
    // shows exactly 2 nodes, but the 2nd (the puzzle's own lastIndex) is now
    // the gold "solved" node (PathTimeline.tsx's isFinal branch) instead of
    // turn 2's real Juventus content -- the timeline's own SolvedNode
    // component, not the ClubReveal turn that would otherwise have occupied
    // that slot.
    await expect(page.locator('.path-timeline__solved-label')).toBeVisible()
    await expect(page.locator('.path-timeline__solved-label')).toContainText('Solved')
    await expect(page.getByText(seed.correctPlayerName)).toBeVisible()
    // PathGuessInput.tsx: isCorrect -> disabled=true, plus its own explicit
    // "Solved — nothing left to guess here." copy (never color/icon-only,
    // per §6).
    await expect(nameField).toBeDisabled()
    await expect(guessButton).toBeDisabled()
    await expect(page.getByText('Solved — nothing left to guess here.')).toBeVisible()
    // REQ-1205 judgment call (PathScreen.tsx's own flagged comment):
    // "Next puzzle" appears whenever a puzzle locks, solved or not -- but
    // this is the round's only puzzle (isLastPuzzle), so PathScreen.tsx
    // shows the completion message instead of a "Next puzzle" button.
    await expect(page.getByText('You’ve completed every puzzle in this round.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Next puzzle' })).not.toBeVisible()

    // ---- Round close (REQ-205, applied to xg-path) ---------------------
    // Same immediate-lock convention play-grid.spec.ts's own 3rd test
    // relies on: force-close-round/{roundId} is game-agnostic and locks
    // scores in immediately rather than waiting for the round's own
    // scheduled EndTime.
    const closeResponse = await request.post(
      `${API_BASE_URL}/internal/test-data/force-close-round/${seed.roundId}`,
    )
    expect(closeResponse.ok(), `force-close-round failed: ${closeResponse.status()}`).toBeTruthy()

    // ---- Leaderboard: game-scoped points, not blended (REQ-410/ADR-0043,
    // REQ-408, REQ-1206) ------------------------------------------------
    //
    // REQ-1206: xG Path is NOT scored like xG Grid's uniqueness/"golf"
    // model (ADR-0020/0021) at all -- ClueEfficiencyScoringStrategy
    // (XGArcade.Core.Scoring, registered for GameKey "xg-path" per
    // ADR-0040/0049) computes FinalPoints = round(cluesUsed /
    // maxAttemptsForCell * ScoringRules.MaxPointsPerCell). cluesUsed is the
    // winning Guess's own AttemptCount at lock time (GuessSubmissionService
    // increments AttemptCount by exactly 1 per submission for a cell,
    // regardless of correctness) -- this puzzle's winning guess was the 2nd
    // submission (1 wrong + 1 correct), so cluesUsed=2,
    // maxAttemptsForCell=7 (REQ-1205's fixed cap), MaxPointsPerCell=100
    // (ScoringRules.cs): round(2 / 7 * 100) = round(28.571...) = 29. This is
    // NOT the xG Grid "lone/only correct guesser scores 0" case
    // (ADR-0020/0021) -- xG Path has no uniqueness signal at all (REQ-1206's
    // own text: "every player who solves a given puzzle names the same
    // target player, so there is no 'how unique was your correct answer'
    // signal for this game").
    await page.getByRole('button', { name: 'Leaderboard' }).click()

    // LeaderboardScreen.tsx defaults to the "xG Grid" game tab and
    // "All-time" scope on mount -- switch to "xG Path" first. Using the
    // Game switcher's own tablist (role="tablist" aria-label="Game"), never
    // HeaderNav's differently-shaped, separately-reachable "xG Path" entry.
    await page.getByRole('tab', { name: 'xG Path' }).click()

    // REQ-408: "Previous Rounds" is used here rather than "All-time"
    // deliberately -- REQ-409's median-of->=5-qualifying-rounds floor (which
    // play-grid.spec.ts's own leaderboard test has to loop 5 rounds to
    // clear) would hide a single-round player from "All-time" entirely,
    // which would prove nothing either way about this REQ. "Previous
    // Rounds" -> a specific closed round's own locked leaderboard
    // (fetchClosedRoundLeaderboard) has no such floor and resolves by
    // roundId alone, so it's both the most direct and the most robust check
    // available for a single freshly-closed round.
    //
    // The GET .../closed-rounds request this triggers is captured directly
    // (rather than relying on round-list button text, whose "Closed
    // {closedAt}" formatting isn't asserted on anywhere else in this repo)
    // to assert on its actual JSON body below -- both a positive check (our
    // round is present, and REQ-408's "most recently closed first" order
    // puts it first, since force-close-round sets ClosedAt to the current
    // moment -- always more recent than any xg-path round closed by an
    // earlier local run or CI job) and, after switching game tabs, the
    // negative "not blended" check ADR-0043 exists to guarantee.
    const xgPathClosedRoundsResponsePromise = page.waitForResponse(
      (response) =>
        response.url().includes('/leagues/global/leaderboard/closed-rounds') &&
        response.url().includes('gameKey=xg-path') &&
        response.request().method() === 'GET',
    )
    await page.getByRole('tab', { name: 'Previous Rounds' }).click()
    const xgPathClosedRoundsResponse = await xgPathClosedRoundsResponsePromise
    const xgPathClosedRoundsBody = (await xgPathClosedRoundsResponse.json()) as {
      rounds: Array<{ roundId: string }>
    }
    expect(xgPathClosedRoundsBody.rounds[0]?.roundId).toBe(seed.roundId)

    // Click the just-verified first entry to drill into its locked detail
    // (LeaderboardScreen.tsx's handleSelectRound -> fetchClosedRoundLeaderboard).
    await page.locator('.leaderboard-screen__round-list-button').first().click()

    const playerRow = page.getByRole('listitem').filter({ hasText: playerDisplayName })
    await expect(playerRow).toBeVisible()
    await expect(playerRow.getByText('29 pts')).toBeVisible()
    // Text, not color-only (design-document.md §6) -- LeaderboardRowsList's
    // own "you" tag for the requesting player's row.
    await expect(playerRow.getByText('you')).toBeVisible()

    // ---- Not blended with xG Grid's leaderboard (REQ-410/ADR-0043) -----
    // Switching the game tab while a round is drilled into resets
    // selectedRound/pastDetailState (LeaderboardScreen.tsx's own
    // prevGameKeyForPastDetailRef effect) and re-fetches the closed-rounds
    // list under the new gameKey. If GameKey scoping were ever broken (e.g.
    // a missing WHERE GameKey = @gameKey clause), this xg-path round would
    // leak into xg-grid's own closed-rounds list -- asserted directly
    // against the response body, not by trying to interpret the round-list
    // UI's opaque "Closed {closedAt}" text.
    const xgGridClosedRoundsResponsePromise = page.waitForResponse(
      (response) =>
        response.url().includes('/leagues/global/leaderboard/closed-rounds') &&
        response.url().includes('gameKey=xg-grid') &&
        response.request().method() === 'GET',
    )
    await page.getByRole('tab', { name: 'xG Grid' }).click()
    const xgGridClosedRoundsResponse = await xgGridClosedRoundsResponsePromise
    const xgGridClosedRoundsBody = (await xgGridClosedRoundsResponse.json()) as {
      rounds: Array<{ roundId: string }>
    }
    expect(xgGridClosedRoundsBody.rounds.some((round) => round.roundId === seed.roundId)).toBe(false)
    // And, as a second, UI-level corroboration of the same guarantee: our
    // xg-path player's display name never appears anywhere under the xG
    // Grid tab -- this player has no xg-grid guesses at all, so a blending
    // bug that merged the two games' totals/rows together would surface
    // here as a stray, otherwise-inexplicable row.
    await expect(page.getByText(playerDisplayName)).not.toBeVisible()
  })
})
