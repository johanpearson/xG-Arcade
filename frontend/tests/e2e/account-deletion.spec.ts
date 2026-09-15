import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// S-247 (docs/backlog.md Epic 33)/REQ-710: the full delete-account flow
// against a real backend — signup, the real UI deletion flow (S-039/
// REQ-713's "Settings" consolidation), a logout assertion, and REQ-710's
// two post-deletion guarantees ("the user can no longer log in" and "their
// email becomes available for a new account"). REQ-710 itself is covered
// at the API level by AuthEndpointTests.cs (S-025) and at the component
// level by DeleteAccountScreen.test.tsx (S-039's wrong-password/cancel/
// captcha-rejection branches) — this spec's job is only the real, full-
// stack happy path plus the two guarantees above; it does not re-test
// those component-level branches.
//
// Matches the pattern already established for the backend base URL (see
// play-grid.spec.ts).
const API_BASE_URL = process.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// The password used throughout this repo's E2E specs (play-grid.spec.ts,
// header-nav.spec.ts, splash-screen.spec.ts).
const PASSWORD = 'password123'

// REQ-701/REQ-806's @test.invalid convention (see play-grid.spec.ts): a
// fresh, unique account per run, created through the real signup endpoint
// (never seeded directly), with the required age checkbox checked.
// AuthScreen auto-logs-in after signup, landing on the game-select screen
// (REQ-303/S-021) — App.tsx renders the header nav on every authenticated
// screen, so no game/round needs to be seeded just to reach Settings.
async function signUpNewPlayer(page: Page, email: string, tag: string): Promise<void> {
  // REQ-717/ADR-0037 follow-up: handleSubmit calls the real
  // getTurnstileToken() unconditionally -- stub window.turnstile before
  // this signup form ever submits (see turnstile-stub.ts). addInitScript()
  // persists across navigations on this page, so it also covers the
  // DeleteAccountScreen form reached later in the same test without being
  // called a second time.
  await stubTurnstile(page)
  await page.goto('/')
  // REQ-719: a fresh, unauthenticated visit lands on the splash screen
  // first, not the login/signup form directly.
  await expect(page.getByTestId('splash-screen')).toBeVisible()
  await page.getByRole('button', { name: 'Log in or sign up' }).click()
  await page.getByRole('tab', { name: 'Sign up' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(PASSWORD)
  await page.getByLabel('Confirm password').fill(PASSWORD)
  await page.getByLabel('Display name').fill(`Delete Test ${tag}`)
  await page.getByLabel(/at least 16 years old/).check()
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByText('Choose a game')).toBeVisible()
}

// Tests run serially and share `deletedEmail` via closure — the second
// test's API-level assertions are about the exact account the first test
// deletes through the real UI, so it must run after it, but it never
// touches the first test's `page`/browser state directly (Playwright gives
// every test its own fresh `page`) — only the plain string email/password
// values cross between them, the same "pass via closure" pattern
// play-grid.spec.ts's shared `previousRoundId` uses for a different reason
// (round sequencing) in its own serial describe block.
test.describe.configure({ mode: 'serial' })

test.describe('REQ-710: account deletion', () => {
  let deletedEmail: string

  test('REQ-710/713: deleting the account via Settings confirms with password and logs the session out to the splash screen', async ({
    page,
  }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-delete-${tag}@test.invalid`
    deletedEmail = email

    await signUpNewPlayer(page, email, tag)

    // REQ-713: the standalone "Delete account" link was consolidated into
    // the single "Settings" nav entry — DeleteAccountScreen is rendered
    // unmodified inside it, at the bottom of the screen (SettingsScreen.tsx).
    // At Playwright's default desktop viewport (well above the 480px
    // REQ-712 breakpoint), the nav buttons are visible directly, with no
    // toggle to activate first.
    await page.getByRole('button', { name: 'Settings' }).click()
    await expect(page.getByRole('heading', { name: 'Delete account' })).toBeVisible()

    // REQ-710: irreversible, so this is surfaced as a real warning, not
    // just implied by the screen's presence.
    await expect(
      page.getByRole('alert').filter({ hasText: 'This permanently deletes your account. It cannot be undone.' }),
    ).toBeVisible()

    // REQ-710's confirmation step: the current password re-entered and
    // re-verified server-side, not a bare confirmation checkbox.
    // getByLabel('Current password') rather than a bare /password/i match —
    // this same Settings screen also has a "Password"-labelled field in its
    // guest-claim section, which a looser match could accidentally hit.
    await page.getByLabel('Current password').fill(PASSWORD)
    await page.getByRole('button', { name: 'Delete my account permanently' }).click()

    // On success, DeleteAccountScreen's onAccountDeleted routes through
    // App.tsx's handleLogout (onAccountDeleted={handleLogout}), landing back
    // on the splash screen exactly like REQ-719's own logout path — same
    // assertion shape as splash-screen.spec.ts's existing "Log out" check.
    await expect(page.getByTestId('splash-screen')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Log in or sign up' })).toBeVisible()
    await expect(page.getByRole('tab', { name: 'Log in' })).not.toBeVisible()
    await expect(page.getByRole('tab', { name: 'Sign up' })).not.toBeVisible()
  })

  // REQ-710's remaining two guarantees ("the user can no longer log in" /
  // "their email becomes available for a new account") — asserted at the
  // API level (request fixture, an APIRequestContext, same as
  // play-grid.spec.ts's signUpAndLoginViaApi helper) rather than by driving
  // the real login form again, for a specific, documented reason below.
  //
  // *** Why this does NOT assert that POST /auth/login rejects the deleted
  // account's credentials ***
  // ci.yml's local E2E stack has no live Supabase project; Program.cs swaps
  // in LocalE2EAuthClient (backend/src/XGArcade.Api/Auth/LocalE2EAuth.cs)
  // for ISupabaseAuthClient. Its SignInWithPasswordAsync (used by both
  // POST /auth/login and DeleteAccount's own password re-confirmation) does
  // no real password check and no existence check at all — it's
  // `Authenticate(email)`, a pure function of the email alone (an
  // MD5-derived deterministic GUID), and it always returns Success = true.
  // This is documented, deliberate test-stack behavior, not a bug — see
  // NOTES.md's 2026-07-09 entry ("call /auth/login with any email/password
  // against the local stack, no Supabase secrets needed"). AuthController
  // .Login also never checks that a local User row exists for the resolved
  // identity before returning 200 with a token — a missing row only skips
  // the best-effort UpdateLastActiveAtAsync call, it doesn't fail the login.
  //
  // Consequence: against this backend, POST /auth/login with the deleted
  // account's exact email/password still returns 200 and a valid-looking
  // JWT — a strict "login returns 401/fails" assertion would test something
  // that isn't true of this stack, and driving the real login *form* would
  // misleadingly land back on "Choose a game" rather than showing an error,
  // since the UI has no way to know the account is gone until it calls an
  // endpoint that actually checks. What genuinely changes post-deletion,
  // and IS reliably assertable here, is GET /auth/me (AuthController.Me):
  // it resolves the JWT's `sub` claim to a local User row and returns 404
  // when that row doesn't exist — which is true immediately after deletion
  // (AccountDeletionService deletes the User row), and was not true a
  // moment before. So: take the token from a post-deletion login attempt
  // and confirm GET /auth/me 404s with it — that's the real,
  // backend-accurate form of "the user can no longer log in" available
  // against this stack, not a workaround for a flaky assertion.
  //
  // The email-freed guarantee has no such fidelity gap: POST /auth/signup
  // again with the exact deleted email is a clean, literal proof that the
  // account (and the uniqueness constraint tied to its email) is truly
  // gone — the strongest signal this spec can give that S-247's intent
  // actually holds.
  test('REQ-710: a post-deletion login token no longer resolves via GET /auth/me, and the email can be re-registered', async ({
    request,
  }: {
    request: APIRequestContext
  }) => {
    expect(deletedEmail, 'the UI deletion test above must run first and set deletedEmail').toBeTruthy()

    // See this test's own top-of-file comment: LocalE2EAuthClient's
    // SignInWithPasswordAsync performs no real password/existence check, so
    // this call succeeds (200) even though the account was just deleted —
    // that is the documented fidelity gap this test works around, not an
    // assertion this test makes.
    const loginResponse = await request.post(`${API_BASE_URL}/auth/login`, {
      data: { email: deletedEmail, password: PASSWORD, captchaToken: 'e2e-test-token' },
    })
    expect(loginResponse.ok(), `post-deletion login failed unexpectedly: ${loginResponse.status()}`).toBeTruthy()
    const { accessToken } = (await loginResponse.json()) as { accessToken: string }

    // The real, backend-accurate proof the account is gone: the token
    // resolves to no local User row.
    const meResponse = await request.get(`${API_BASE_URL}/auth/me`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    })
    expect(meResponse.status(), 'GET /auth/me should 404 once the User row is deleted').toBe(404)

    // REQ-710's "email becomes available for a new account" bullet — a
    // fresh signup with the exact same email, a different password/display
    // name (need not match the original), succeeds cleanly.
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const resignupResponse = await request.post(`${API_BASE_URL}/auth/signup`, {
      data: {
        email: deletedEmail,
        password: 'password456',
        confirmPassword: 'password456',
        displayName: `Re Registered ${tag}`,
        ageConfirmed: true,
        captchaToken: 'e2e-test-token',
      },
    })
    expect(
      resignupResponse.status(),
      `re-registering the freed email failed: ${resignupResponse.status()} ${await resignupResponse.text()}`,
    ).toBe(201)
  })
})
