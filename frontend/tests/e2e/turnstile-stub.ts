import type { Page } from '@playwright/test'
import type { TurnstileApi } from '../../src/lib/turnstile'

// REQ-717/ADR-0037 follow-up (2026-07-25 captcha-bug-fix quality-gate
// finding): AuthScreen.tsx's handleSubmit now unconditionally calls the
// real getTurnstileToken() (frontend/src/lib/turnstile.ts) before every
// login/signup submission, in every environment -- there is no dev/E2E
// bypass in production code, by design (see that file's own top-of-file
// comment). ci.yml's e2e-tests job never sets VITE_TURNSTILE_SITE_KEY
// (only deploy.yml does), so without this stub, loadTurnstileScript()
// would try to inject Cloudflare's real
// <script src="https://challenges.cloudflare.com/turnstile/v0/api.js">
// tag and render a widget against an empty site key -- which can't mint a
// token, leaving getTurnstileToken()'s promise rejected/hanging and every
// UI-driven login/signup spec unable to ever reach the screen it actually
// tests.
//
// window.turnstile is stubbed via page.addInitScript() -- addInitScript()
// runs before the page's own scripts on every navigation on that page
// (including a later page.reload(), per Playwright's own documented
// semantics), so by the time AuthScreen.tsx's module code runs,
// window.turnstile already exists. loadTurnstileScript() (turnstile.ts)
// only appends the real <script> tag when window.turnstile is NOT already
// present at call time (`if (window.turnstile) { resolve(...); return }`),
// so stubbing it here also prevents any real network call to Cloudflare --
// confirmed by reading that function directly, not assumed. Callers MUST
// call this before their first page.goto()/page.reload() on a given page;
// calling it after navigation has already started is too late for that
// page load.
//
// The stub's shape matches turnstile.ts's real TurnstileApi contract
// exactly (render/reset/remove). render() invokes options.callback(...)
// on a resolved-microtask tick with a fixed fake token, matching how
// getTurnstileToken() awaits that callback via its own `new Promise`
// wrapper -- same intent as AuthScreen.test.tsx/App.test.tsx's
// vi.mock('../lib/turnstile', ...) stand-in at the module level, just done
// at the browser-injection level Playwright's real, non-jsdom pages
// require instead.
//
// This does NOT test REQ-717/ADR-0037's captcha behavior itself (there is
// no live Cloudflare site key in this sandbox/CI to test against for real,
// and this stub always "succeeds") -- it exists purely so specs that only
// need an authenticated session to reach an unrelated screen under test
// aren't blocked by a captcha widget that can never mint a real token in
// CI. Nothing under frontend/tests/e2e/ asserts on captcha
// rejection/success paths themselves.
export async function stubTurnstile(page: Page): Promise<void> {
  await page.addInitScript(() => {
    let nextWidgetId = 0
    const stub: TurnstileApi = {
      render: (_container, options) => {
        const widgetId = `stub-widget-${nextWidgetId++}`
        // Resolved-microtask tick rather than a synchronous call -- mirrors
        // a real widget's async callback invocation. getTurnstileToken()
        // awaits the callback via `new Promise`, so either timing would
        // resolve it, but this avoids relying on that.
        Promise.resolve().then(() => options.callback?.('e2e-stub-turnstile-token'))
        return widgetId
      },
      reset: () => {},
      remove: () => {},
    }
    ;(window as unknown as { turnstile: TurnstileApi }).turnstile = stub
  })
}
