import { expect, test, type Page } from '@playwright/test'
import { stubTurnstile } from './turnstile-stub'

// REQ-721/ADR-0039: hash-based, hand-rolled URL-per-screen support — see
// that ADR for why (hash not path, no router library, no popstate/
// hashchange listener; browser back/forward is explicitly out of scope).
// App.test.tsx (src/App.test.tsx) already covers the ordering constraints
// (a fresh login always lands on game-select regardless of the URL, a
// reload with no valid session never restores an authenticated screen) at
// the jsdom level; this file is the real-browser half of REQ-721's own
// "Test level" line — a real page.reload() against a real dev server,
// which jsdom can only simulate by remounting <App />, not by actually
// re-reading `location.hash` from a fresh navigation.
async function signUpNewPlayer(page: Page): Promise<void> {
  const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
  const email = `test-url-${tag}@test.invalid`

  await stubTurnstile(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'Log in or sign up' }).click()
  await page.getByRole('tab', { name: 'Sign up' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill('password123')
  await page.getByLabel('Confirm password').fill('password123')
  await page.getByLabel('Display name').fill(`URL Test ${tag}`)
  await page.getByLabel(/at least 16 years old/).check()
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByText('Choose a game')).toBeVisible()
}

test.describe('REQ-721: current screen reflected in the URL; a reload restores it', () => {
  test('REQ-721: navigating through several screens changes the URL each time', async ({ page }) => {
    await signUpNewPlayer(page)
    await expect(page).toHaveURL(/#\/game-select$/)

    await page.getByRole('button', { name: 'Settings' }).click()
    await expect(page).toHaveURL(/#\/settings$/)

    await page.getByRole('button', { name: 'Leagues' }).click()
    await expect(page).toHaveURL(/#\/leagues$/)

    // The title always returns to game-select, unaffected by REQ-721 —
    // still updates the hash to match.
    await page.getByRole('button', { name: 'xG Arcade' }).click()
    await expect(page).toHaveURL(/#\/game-select$/)
  })

  test('REQ-721: reloading on an authenticated screen with a valid session restores that same screen, not the game-select default', async ({
    page,
  }) => {
    await signUpNewPlayer(page)

    await page.getByRole('button', { name: 'Settings' }).click()
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
    await expect(page).toHaveURL(/#\/settings$/)

    await page.reload()

    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
    await expect(page.getByText('Choose a game')).not.toBeVisible()
  })

  test('REQ-721: reloading while logged out shows the splash screen regardless of what URL was requested', async ({
    page,
  }) => {
    await signUpNewPlayer(page)
    await page.getByRole('button', { name: 'Settings' }).click()
    await expect(page).toHaveURL(/#\/settings$/)

    await page.getByRole('button', { name: 'Log out' }).click()
    await expect(page.getByTestId('splash-screen')).toBeVisible()

    // A stale/guessed authenticated-screen URL, requested with no valid
    // session at all — never bypasses REQ-719's splash gate.
    await page.goto('/#/settings')
    await expect(page.getByTestId('splash-screen')).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Settings' })).not.toBeVisible()
  })

  test('REQ-721: completing a fresh login always lands on game-select regardless of the URL present immediately before submitting', async ({
    page,
  }) => {
    const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
    const email = `test-url-login-${tag}@test.invalid`

    await stubTurnstile(page)
    await page.goto('/')
    await page.getByRole('button', { name: 'Log in or sign up' }).click()
    await page.getByRole('tab', { name: 'Sign up' }).click()
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password', { exact: true }).fill('password123')
    await page.getByLabel('Confirm password').fill('password123')
    await page.getByLabel('Display name').fill(`URL Login Test ${tag}`)
    await page.getByLabel(/at least 16 years old/).check()
    await page.getByRole('button', { name: 'Create account' }).click()
    await expect(page.getByText('Choose a game')).toBeVisible()

    await page.getByRole('button', { name: 'Log out' }).click()
    await expect(page.getByTestId('splash-screen')).toBeVisible()

    // Manually set an unrelated hash before logging back in — a fresh
    // login must ignore it and always land on game-select (REQ-303/S-021).
    await page.evaluate(() => {
      window.location.hash = '#/settings'
    })

    await page.getByRole('button', { name: 'Log in or sign up' }).click()
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill('password123')
    await page.getByRole('button', { name: 'Log in' }).click()

    await expect(page.getByText('Choose a game')).toBeVisible()
    await expect(page).toHaveURL(/#\/game-select$/)
  })
})
