import { expect, test } from '@playwright/test'

// S-002 (trivial end-to-end slice, docs/backlog.md): proves the deployed/
// locally-run frontend actually reaches the real backend's /health endpoint
// end-to-end, not just that the page renders. No REQ-xxx exists for this
// (pure infra), unlike feature tests elsewhere in the suite.
test('shows the API health status', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByTestId('health-status')).toHaveText('healthy')
})
