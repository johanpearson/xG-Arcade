import { expect, test } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// REQ-719: the unauthenticated splash/landing screen shown before AuthScreen
// — this file is the E2E half of REQ-719's "Test level" line (component
// coverage for the render-instead-of-AuthScreen/CTA-navigates/
// logout-returns-to-splash assertions already lives in App.test.tsx). What
// only a real browser round trip proves is the full visitor journey: a
// fresh visit lands on the splash screen, its call-to-action actually
// reaches and can complete a real login/signup, and logging out returns to
// the splash screen rather than a dead end — from which logging back in
// remains reachable.
test('REQ-719: a fresh unauthenticated visit shows the splash screen first, the call-to-action reaches and completes signup, and logging out returns to the splash screen (not a dead end)', async ({
  page,
}) => {
  const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
  const email = `test-splash-${tag}@test.invalid`

  // REQ-717/ADR-0037 follow-up: AuthScreen's handleSubmit calls the real
  // getTurnstileToken() unconditionally -- stub window.turnstile before
  // this signup form ever submits (see turnstile-stub.ts).
  await stubTurnstile(page)
  await page.goto('/')

  // A fresh, fully unauthenticated visit lands on the splash screen, not
  // the login/signup form directly.
  await expect(page.getByTestId('splash-screen')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Log in or sign up' })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Sign up' })).not.toBeVisible()
  await expect(page.getByRole('tab', { name: 'Log in' })).not.toBeVisible()

  // The single call-to-action reaches AuthScreen, where signup/login can
  // still be completed exactly as before.
  await page.getByRole('button', { name: 'Log in or sign up' }).click()
  await expect(page.getByRole('tab', { name: 'Sign up' })).toBeVisible()

  await page.getByRole('tab', { name: 'Sign up' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill('password123')
  await page.getByLabel('Confirm password').fill('password123')
  await page.getByLabel('Display name').fill(`Splash Test ${tag}`)
  await page.getByLabel(/at least 16 years old/).check()
  await page.getByRole('button', { name: 'Create account' }).click()

  // REQ-303/S-021's existing post-login routing is unchanged by this
  // requirement.
  await expect(page.getByText('Choose a game')).toBeVisible()

  // Logging out returns to the splash screen, not directly to AuthScreen —
  // never a dead end, since the same call-to-action is right there again.
  await page.getByRole('button', { name: 'Log out' }).click()
  await expect(page.getByTestId('splash-screen')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Log in or sign up' })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Log in' })).not.toBeVisible()

  // Logging back in from there still works.
  await page.getByRole('button', { name: 'Log in or sign up' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('password123')
  await page.getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByText('Choose a game')).toBeVisible()
})
