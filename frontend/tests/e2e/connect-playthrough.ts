import { expect, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// Shared xG Connect helpers, extracted from play-connect.spec.ts (S-246,
// docs/backlog.md) so friends-challenges.spec.ts can reuse the same
// play-through logic (sign-up, API login/id lookup, deterministic
// target/connector player seeding, and the target-pick/chain-closing steps
// themselves) rather than duplicating it. play-connect.spec.ts remains the
// canonical, most heavily-commented reference for exactly WHY each of these
// steps behaves the way it does (e.g. the "Connected!" race-condition
// history below) — comments that explain THIS module's own behavior live
// here; comments about a specific *spec's* own scenario stay in that spec.
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// Matches backend/src/XGArcade.Api/Connect/InternalConnectTestDataEndpoints.cs's
// SeedConnectPlayersResponse record exactly (System.Text.Json's default
// camelCase policy). See that file's own top-of-file comment for exactly
// why this endpoint exists, including the real cross-boundary id-space bug
// (REQ-1404, now fixed on both backend and frontend) it originally had to
// work around before target-pick resolution moved to name-based lookup —
// this endpoint's PlayerNameIndex seeding step remains useful afterward for
// an unrelated reason, noted again at each call site's own target-pick step.
export interface SeedConnectPlayersResponse {
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

// REQ-701/REQ-806's real-signup-endpoint convention (see play-grid.spec.ts/
// play-path.spec.ts's own identical helper) — a fresh, unique @test.invalid
// account per player, created and auto-logged-in through the real UI,
// landing on GameSelectScreen ("Choose a game").
export async function signUpNewConnectPlayer(page: Page, displayName: string, email: string): Promise<void> {
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

// A second, API-only login for the SAME account a UI signup (or an
// API-only signup) just created (same email/password) — purely to get a
// Bearer token for direct setup calls. Same "probe login via the request
// context" shape as play-path.spec.ts's clearAnyExistingActivePathRound
// helper, just reused for a real, already-signed-up player instead of a
// throwaway probe.
export async function loginForApi(request: APIRequestContext, email: string): Promise<string> {
  const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
    data: { email, password: 'password123', captchaToken: 'e2e-test-token' },
  })
  expect(loginResponse.ok(), `login failed: ${loginResponse.status()}`).toBeTruthy()
  const { accessToken } = (await loginResponse.json()) as { accessToken: string }
  return accessToken
}

export async function fetchOwnUserId(request: APIRequestContext, accessToken: string): Promise<string> {
  const meResponse = await request.get(`${API_BASE_URL}/auth/me`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  })
  expect(meResponse.ok(), `GET /auth/me failed: ${meResponse.status()}`).toBeTruthy()
  const me = (await meResponse.json()) as MeResponse
  return me.id
}

// ---- Deterministic target/connector players (REQ-1404/1406) -------------
// See InternalConnectTestDataEndpoints.cs's own top-of-file comment for
// exactly how these three players/two clubs are constructed so that (a)
// Target A and Target B are NOT trivially connected, and (b) the one
// connector closes either target's one-step chain symmetrically. It also
// seeds a PlayerNameIndex row per target player so /players/autocomplete
// has something to suggest for the target-pick step below to select
// through the real UI — target-pick resolution itself is by name
// (COMP-06), not by that row's PlayerId, since REQ-1404's id-space
// mismatch bug was fixed. No live Wikidata reachability is needed anywhere
// this is used.
export async function seedConnectPlayers(request: APIRequestContext): Promise<SeedConnectPlayersResponse> {
  const seedResponse = await request.post(`${API_BASE_URL}/internal/test-data/seed-connect-players`)
  expect(seedResponse.ok(), `seed-connect-players failed: ${seedResponse.status()}`).toBeTruthy()
  return (await seedResponse.json()) as SeedConnectPlayersResponse
}

// ---- Target-pick phase (REQ-1404) ----------------------------------------
// TargetPickPanel.tsx requires selecting a real `/players/autocomplete`
// suggestion before "Set target pick" is enabled at all — this is why
// seedConnectPlayers above seeds a PlayerNameIndex row for each target
// player. The submission itself sends the selected suggestion's NAME,
// resolved server-side against Player/COMP-06 (REQ-1404's id-space
// mismatch fix) — this is now the same real path a genuine, Wikidata-
// imported player selection would take, not a test-only workaround.
// Deliberately no shared post-submit assertion inside this helper: the UI
// genuinely diverges after submitting depending on whether this is the
// first or the completing (second) target pick (see
// ConnectTargetPickService.SubmitTargetPickAsync — both rows only flip
// `locked` together, atomically, on the SECOND submission). Each call site
// asserts what its own player's screen actually shows next.
export async function submitTargetPick(page: Page, name: string): Promise<void> {
  await page.getByLabel('Target player name').fill(name)
  await page.getByRole('option', { name }).click()
  await page.getByRole('button', { name: 'Set target pick' }).click()
}

// ---- Closing chain-step submission (REQ-1406/1407/1408) ------------------
// `connectorPlayerName` is an explicit parameter (rather than closing over
// a `seed` result the way play-connect.spec.ts's own original inline copy
// did) so this helper is reusable across specs/fixtures without requiring
// a specific in-scope variable name.
//
// Design change (2026-09-04, REQ-1406, ADR-0104): the player no longer
// types a claimed club — only the candidate name — so this helper doesn't
// take one either; the server computes which club(s) actually connect the
// two players.
// Bug fix (2026-09-05, ADR-0107): a real /players/autocomplete suggestion
// must now be clicked (same requirement submitTargetPick above already
// has) — typing the name alone no longer enables "Submit connector," since
// that free-text path is exactly the same-name-collision-prone one a real
// incident showed is a genuine bug. InternalConnectTestDataEndpoints now
// seeds a matching PlayerNameIndex row (with WikidataQid) for the
// connector player too, so this suggestion is always findable here.
export async function submitClosingChainStep(page: Page, connectorPlayerName: string): Promise<void> {
  await page.getByLabel('Candidate player name').fill(connectorPlayerName)
  await page.getByRole('option', { name: connectorPlayerName }).click()
  // Captured (not awaited) BEFORE the click below, purely for diagnostics
  // on failure — recording the promise doesn't delay or otherwise change
  // the click/assert timing that follows, since nothing here awaits it
  // unless the "Connected!" assertion below actually fails.
  const chainStepResponsePromise = page.waitForResponse(
    (response) => response.url().includes('/chain-steps') && response.request().method() === 'POST',
  )
  await page.getByRole('button', { name: 'Submit connector' }).click()
  // 2026-09-04 CONFIRMED root cause and fix (this assertion's own CI trail
  // — four failures across play-connect.spec.ts's history, the fourth
  // genuine bug this E2E spec has caught): "Connected!" was ORIGINALLY set
  // from local React state the instant the POST response arrived
  // (ChainBuilder.tsx's own handleSubmit), with no dependency on the
  // follow-up refetch. That was fragile in exactly one real scenario,
  // caught with diagnostic logging on a real CI run: when THIS submission
  // is also the one that completes match resolution (the submitter's
  // opponent had already reached their own terminal state first),
  // `ConnectChainStepService.SubmitChainStepAsync` resolves the match
  // server-side INLINE in the same request, so the very next
  // `onChanged()`-triggered refetch comes back `status: 'Resolved'` —
  // MatchScreen.tsx immediately swaps ChainBuilder out for
  // MatchResolution, wiping ChainBuilder's local `feedback` state,
  // sometimes before that state was ever painted at all. Real product bug,
  // not a test artifact: a real player closing the completing connector
  // could see the same zero-perceptible-time flash (or nothing).
  //
  // Fixed on both sides of that swap: ChainBuilder.tsx now derives this
  // acknowledgment from `myTerminalState.completed` (refreshed props,
  // durable across re-renders) instead of one-shot local state, for the
  // non-resolving case; MatchResolution.tsx now shows the same
  // acknowledgment itself, derived from the same field in the resolved-
  // match payload, for the resolving case — see both components' own
  // S-218 comments. Either way, this assertion now depends on a real `GET
  // /matches/{matchId}` round trip completing (the POST's own follow-up
  // refetch), not an instantaneous local-state flip — the generous 20s
  // timeout below covers that legitimately, on top of covering the plain
  // CI resource-contention flake this wait was originally widened for.
  try {
    await expect(page.getByText('Connected! Your chain is complete.')).toBeVisible({ timeout: 20_000 })
  } catch (err) {
    const response = await chainStepResponsePromise.catch(() => null)
    const bodyText = response ? await response.text().catch(() => '<unreadable body>') : '<no response observed>'
    console.error(
      `submitClosingChainStep: "Connected!" never appeared for connector="${connectorPlayerName}". ` +
        `POST /chain-steps responded ${response?.status() ?? '<none>'}: ${bodyText}`,
    )
    throw err
  }
}
