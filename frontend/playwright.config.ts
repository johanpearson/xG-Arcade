import { defineConfig, devices } from '@playwright/test'

// ci.yml starts the API manually and relies on this config's webServer to
// boot the Vite dev server on :5173 itself (see the workflow's E2E step).
export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: true,
  // S-245: header-nav.spec.ts's REQ-720 "no active round" check asserts a
  // suite-wide condition (GET /rounds/current's 404 empty state for the
  // whole xG Grid GameKey), which every round-seeding spec (play-grid,
  // play-path, play-predict, play-higher-lower, and now admin-review) can
  // violate for the whole duration it holds a round open. Under
  // fullyParallel scheduling with >1 worker this was always racy — it just
  // never collided before because those specs happened to sort later
  // alphabetically and finish (or start) too late/early to overlap with
  // header-nav.spec.ts's own fast test. admin-review.spec.ts sorts first
  // and holds two rounds open for nearly its whole (multi-live-Wikidata-
  // round-trip) duration, making the collision deterministic (reproduced
  // identically on two separate real ci.yml runs, same assertion, same
  // line, both times). Serializing CI's own run closes this whole class of
  // shared-global-round-state race for every current and future spec, not
  // just this one — the ~2x wall-clock cost (a small suite, seconds not
  // minutes) is worth it over a test suite that fails non-deterministically
  // depending on file-queue luck. Local runs keep Playwright's own
  // auto-detected worker count (undefined), since a developer running one
  // spec at a time locally doesn't hit this cross-file race.
  workers: process.env.CI ? 1 : undefined,
  // CI additionally gets a JUnit file so ci.yml can publish a Checks-tab
  // test report (dorny/test-reporter) alongside the usual HTML report.
  reporter: process.env.CI ? [['html'], ['junit', { outputFile: 'test-results/e2e-junit.xml' }]] : 'html',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5173',
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        // This environment's browser install predates the
        // @playwright/test version pinned in package.json — point at it
        // explicitly rather than downloading a second copy.
        launchOptions: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
          ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE }
          : undefined,
      },
    },
  ],
  webServer: {
    command: 'npm run dev',
    url: 'http://localhost:5173',
    reuseExistingServer: !process.env.CI,
  },
})
