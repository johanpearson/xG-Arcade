import { expect, test, type Page } from '@playwright/test'

// REQ-712: the header nav's mobile-collapse behavior only actually applies
// at real viewport widths — HeaderNav.css's `@media (max-width: 480px)`
// block is what decides which of the toggle button or the plain horizontal
// row is visible, and jsdom's CSS engine (App.test.tsx/tests/unit) never
// applies `@media`-scoped rules regardless of window width, so that suite
// only covers the toggle's aria-expanded mechanism, not the actual
// responsive layout. This file is the real-viewport half of REQ-712's own
// "Test level" line.
const NARROW_VIEWPORT = { width: 375, height: 812 } // below the 480px breakpoint
const DESKTOP_VIEWPORT = { width: 1280, height: 800 } // Playwright's usual default-ish desktop size, well above the breakpoint

// REQ-701/REQ-806's @test.invalid convention (see play-grid.spec.ts): a
// fresh, unique account per test via the real signup endpoint, never seeded
// directly. AuthScreen auto-logs-in after signup, landing on the
// game-select screen — App.tsx renders the header on every authenticated
// screen, so no game/round needs to be seeded just to exercise the header.
async function signUpNewPlayer(page: Page): Promise<void> {
  const tag = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
  const email = `test-nav-${tag}@test.invalid`

  await page.goto('/')
  await page.getByRole('tab', { name: 'Sign up' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill('password123')
  await page.getByLabel('Confirm password').fill('password123')
  await page.getByLabel('Display name').fill(`Nav Test ${tag}`)
  await page.getByLabel(/at least 16 years old/).check()
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByText('Choose a game')).toBeVisible()
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const { scrollWidth, clientWidth } = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(scrollWidth).toBeLessThanOrEqual(clientWidth)
}

// Presses Tab up to maxPresses times, stopping as soon as `target` has
// focus. Asserts it was reached within that bound rather than hard-coding
// an exact tab-stop count, so this doesn't become brittle against
// unrelated header/DOM changes elsewhere on the page.
async function tabUntilFocused(page: Page, target: ReturnType<Page['getByTestId']>, maxPresses = 10): Promise<void> {
  for (let i = 0; i < maxPresses; i++) {
    if (await target.evaluate((el) => el === document.activeElement)) return
    await page.keyboard.press('Tab')
  }
  await expect(target).toBeFocused()
}

test.describe('REQ-712: header nav collapses behind a menu toggle below the mobile breakpoint', () => {
  test('REQ-712: below 480px, only the toggle is visible; activating it reveals Leaderboard/Settings/Log out, with no horizontal overflow', async ({
    page,
  }) => {
    await page.setViewportSize(NARROW_VIEWPORT)
    await signUpNewPlayer(page)

    const toggle = page.getByTestId('header-nav-toggle')
    await expect(toggle).toBeVisible()
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')

    // The plain nav items are not visible as top-level items before the
    // toggle is activated.
    await expect(page.getByRole('button', { name: 'Leaderboard' })).not.toBeVisible()
    await expect(page.getByRole('button', { name: 'Settings' })).not.toBeVisible()
    await expect(page.getByRole('button', { name: 'Log out' })).not.toBeVisible()

    await expectNoHorizontalOverflow(page)

    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-expanded', 'true')

    await expect(page.getByRole('button', { name: 'Leaderboard' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Settings' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Log out' })).toBeVisible()

    await expectNoHorizontalOverflow(page)
  })

  // REQ-712: "the toggle control is a real, focusable, keyboard-operable
  // element (reachable via Tab, activated via Enter/Space)" — the same
  // accessible-disclosure pattern GridCell.test.tsx's REQ-212
  // "keyboard activation (Enter)"/"keyboard activation (Space)" tests
  // already establish for this codebase, but exercised here (real browser,
  // real narrow viewport) rather than in HeaderNav.test.tsx's jsdom suite:
  // the toggle's CSS visibility is gated by an `@media (max-width: 480px)`
  // rule that jsdom never evaluates (see HeaderNav.test.tsx and
  // App.test.tsx's REQ-712 comments), so Tab-reachability can only be
  // verified where the toggle is actually rendered visible.
  test('REQ-712: below 480px, the toggle is reachable via Tab and activates the menu via Enter and Space', async ({ page }) => {
    await page.setViewportSize(NARROW_VIEWPORT)
    await signUpNewPlayer(page)

    const toggle = page.getByTestId('header-nav-toggle')
    await expect(toggle).toBeVisible()
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')

    await tabUntilFocused(page, toggle)
    await expect(toggle).toBeFocused()

    await page.keyboard.press('Enter')
    await expect(toggle).toHaveAttribute('aria-expanded', 'true')
    await expect(page.getByRole('button', { name: 'Leaderboard' })).toBeVisible()

    await page.keyboard.press('Enter')
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')

    await expect(toggle).toBeFocused()
    await page.keyboard.press('Space')
    await expect(toggle).toHaveAttribute('aria-expanded', 'true')
    await expect(page.getByRole('button', { name: 'Leaderboard' })).toBeVisible()
  })

  test('REQ-712: at or above 480px, the toggle is absent and every nav entry is visible as a horizontal row with no wrapping or overflow', async ({
    page,
  }) => {
    await page.setViewportSize(DESKTOP_VIEWPORT)
    await signUpNewPlayer(page)

    await expect(page.getByTestId('header-nav-toggle')).not.toBeVisible()

    const leaderboardLink = page.getByRole('button', { name: 'Leaderboard' })
    const settingsLink = page.getByRole('button', { name: 'Settings' })
    const logoutLink = page.getByRole('button', { name: 'Log out' })
    await expect(leaderboardLink).toBeVisible()
    await expect(settingsLink).toBeVisible()
    await expect(logoutLink).toBeVisible()

    // No wrapping onto a second line: all three sit on the same
    // horizontal band (same vertical center, within a small tolerance).
    const leaderboardBox = await leaderboardLink.boundingBox()
    const settingsBox = await settingsLink.boundingBox()
    const logoutBox = await logoutLink.boundingBox()
    expect(leaderboardBox).not.toBeNull()
    expect(settingsBox).not.toBeNull()
    expect(logoutBox).not.toBeNull()
    if (leaderboardBox && settingsBox && logoutBox) {
      const leaderboardCenterY = leaderboardBox.y + leaderboardBox.height / 2
      const settingsCenterY = settingsBox.y + settingsBox.height / 2
      const logoutCenterY = logoutBox.y + logoutBox.height / 2
      expect(Math.abs(leaderboardCenterY - settingsCenterY)).toBeLessThan(5)
      expect(Math.abs(leaderboardCenterY - logoutCenterY)).toBeLessThan(5)
    }

    await expectNoHorizontalOverflow(page)
  })
})
