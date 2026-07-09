import { expect, test } from '@playwright/test'

// Placeholder for S-001 (repo/pipeline skeleton, docs/backlog.md) — no
// REQ-xxx exists yet for this project's actual behavior, so this proves
// the Playwright pipeline boots the app and loads a page until the real
// health-check slice (S-002) and grid UI (S-010) land.
test('app loads in the browser', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByText('Get started')).toBeVisible()
})
