// REQ-717's 2026-07-21 "Bot-check (captcha) for guest creation" addition /
// ADR-0037 (amended 2026-07-25, sign-in latency investigation): a small,
// promise-based wrapper around Cloudflare Turnstile's client-side widget/JS
// so callers never juggle the script tag or the imperative `window.turnstile`
// API directly, and so this module can be mocked wholesale in tests rather
// than requiring a live Cloudflare site (untestable in this sandbox -- see
// this module's own test file).
//
// Widget mode: **always-visible checkbox (`size: 'normal'`), not
// invisible/managed.** This reverses ADR-0037's original invisible-mode
// recommendation -- a deliberate product decision, not a bug fix. Two real
// reasons: (a) an invisible widget renders nothing at all while it's
// verifying, which reads as the app being stuck/broken rather than
// "checking something," directly implicated in the 2026-07-25 sign-in
// latency investigation (see NOTES.md/infra/README.md); (b) a genuinely
// *invisible*-type Turnstile site cannot fall back to an interactive
// challenge if Cloudflare's risk scoring is unsure -- it just fails, with no
// escape hatch for the person -- whereas a visible checkbox lets an
// ambiguous case resolve with one tap. The stale "renders no visible UI"
// framing that used to live here described invisible mode; it no longer
// applies now that every render is a real, visible checkbox.
//
// Script preload vs. widget render/token mint are two genuinely different
// operations here, not the same step split in two for no reason:
// `preloadTurnstileScript()` only starts the `<script>` download (safe to
// call as early as component-mount, since it mints no token and renders no
// widget), while `getTurnstileToken()` still does the actual widget
// render + token mint, only ever at submit time. Turnstile tokens are
// single-use and expire quickly against Supabase's own verification (see
// AuthScreen.tsx's signup-then-auto-login comment for the concrete case this
// already forced a design around) -- minting one before the person has
// finished the form risks it going stale by submit time, surfacing as a
// confusing captcha rejection. Preloading only the script avoids that risk
// entirely while still moving the slow part (downloading
// challenges.cloudflare.com/turnstile/v0/api.js, if not already cached) out
// of the serial critical path in front of the actual submit.
//
// The site key is public and safe in frontend code -- same
// `import.meta.env.VITE_*` convention `frontend/src/lib/api.ts` already uses
// for `VITE_API_BASE_URL` (ADR-0037's configuration-split decision). The
// Turnstile *secret* key never appears anywhere in this codebase (it lives
// solely in Supabase's own Auth dashboard settings) -- see ADR-0037's "For
// AI agents" section before ever adding one here.
const SITE_KEY = import.meta.env.VITE_TURNSTILE_SITE_KEY ?? '';

const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js';

interface TurnstileRenderOptions {
  sitekey: string;
  size?: 'invisible' | 'normal' | 'compact';
  callback?: (token: string) => void;
  'error-callback'?: () => void;
  'expired-callback'?: () => void;
}

export interface TurnstileApi {
  render: (container: string | HTMLElement, options: TurnstileRenderOptions) => string;
  reset: (widgetId?: string) => void;
  remove: (widgetId: string) => void;
}

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

let scriptLoadPromise: Promise<TurnstileApi> | null = null;
// The rendered widget's id, or null when a fresh render is needed (either
// because none has been rendered yet, or because resetTurnstileWidget below
// discarded the previous one after a captcha rejection).
let widgetId: string | null = null;
// The in-flight getTurnstileToken() call, or null when none is pending.
// Without this, a second call while one is still awaiting its callback
// would tear down the first call's widget (see the "one widget at a time"
// comment below) before Cloudflare ever invokes that widget's
// callback/error-callback/expired-callback -- leaving the first call's
// promise permanently unresolved rather than rejected. Deduping to the
// same in-flight promise (rather than, say, rejecting the second call)
// means every caller during that window gets the same, eventually-settled
// result, and is the simplest option that needs no new rejection contract
// for callers to handle.
let pendingTokenPromise: Promise<string> | null = null;

// Loads Cloudflare's script exactly once per page load, however many times
// this module's exports are called -- a second/third call reuses the same
// in-flight or already-resolved promise rather than injecting a second
// <script> tag.
function loadTurnstileScript(): Promise<TurnstileApi> {
  if (scriptLoadPromise) return scriptLoadPromise;

  scriptLoadPromise = new Promise((resolve, reject) => {
    if (window.turnstile) {
      resolve(window.turnstile);
      return;
    }
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    script.async = true;
    script.defer = true;
    script.onload = () => {
      if (window.turnstile) resolve(window.turnstile);
      else reject(new Error('Turnstile script loaded but window.turnstile is unavailable.'));
    };
    script.onerror = () => reject(new Error('Failed to load the Turnstile verification script.'));
    document.head.appendChild(script);
  });

  return scriptLoadPromise;
}

// Sign-in latency fix (2026-07-25): starts the Cloudflare script download in
// the background as early as a screen mounts (AuthScreen.tsx/
// DeleteAccountScreen.tsx call this from a mount-only `useEffect`), well
// before the person has clicked anything. Deliberately does NOT render a
// widget or mint a token -- see this file's top-of-file comment for why that
// has to stay deferred to getTurnstileToken() at submit time. Failures are
// swallowed here on purpose: a preload is a pure optimization, and
// `loadTurnstileScript`'s cached `scriptLoadPromise` means a later
// getTurnstileToken() call awaits this exact same promise anyway (rejected
// or not) and surfaces that rejection itself at the moment it actually
// matters -- there is nothing useful for a caller of preload to do with the
// same rejection twice.
export function preloadTurnstileScript(): void {
  loadTurnstileScript().catch(() => {
    // Intentionally ignored -- see comment above.
  });
}

// REQ-717: obtains one Cloudflare Turnstile token before a guarded action
// (login, signup, guest sign-in, account deletion) sends its request. Always
// renders into the `container` the caller supplies -- callers own where that
// container sits in their own screen's layout (visual placement, spacing)
// since this module has no opinion on any one screen's layout, only on the
// widget's behavior. This replaced an earlier version that silently owned a
// single hidden `<div>` appended to `document.body` for invisible-mode
// rendering; that's no longer viable now that the widget is a real, visible
// checkbox the person needs to see and tap in the right place on whichever
// screen invoked it.
//
// Any widget instance left over from a previous, already-settled call is
// torn down first (Cloudflare's render() is not documented as safe to call
// twice into the same container without doing so) and a fresh one is
// rendered every time -- this makes every call, not only the one after
// resetTurnstileWidget(), get a genuinely new execution rather than relying
// on an assumption about how an already-rendered widget behaves on a second
// callback. Never resolves with a placeholder/empty token; a script load
// failure or a Turnstile-reported error rejects instead.
//
// Concurrent calls (a caller invoking this again before a previous call has
// settled) reuse that same in-flight promise instead of racing it: without
// this guard, the second call's widget teardown above would remove the
// first call's still-pending widget out from under it, and the first
// call's promise would never resolve or reject at all (see pendingTokenPromise's
// own comment).
export function getTurnstileToken(container: HTMLElement): Promise<string> {
  if (pendingTokenPromise) return pendingTokenPromise;

  pendingTokenPromise = (async () => {
    try {
      const turnstile = await loadTurnstileScript();

      if (widgetId !== null) {
        turnstile.remove(widgetId);
        widgetId = null;
      }

      return await new Promise<string>((resolve, reject) => {
        widgetId = turnstile.render(container, {
          sitekey: SITE_KEY,
          size: 'normal',
          callback: resolve,
          'error-callback': () => reject(new Error('Could not verify you are not a bot. Please try again.')),
          'expired-callback': () => reject(new Error('Verification expired. Please try again.')),
        });
      });
    } finally {
      // Cleared once settled (either way) so the *next*, non-overlapping
      // call starts a fresh render rather than reusing a resolved/rejected
      // promise forever.
      pendingTokenPromise = null;
    }
  })();

  return pendingTokenPromise;
}

// REQ-717's explicit acceptance criterion: on the backend's distinct
// captcha-rejection response, the frontend must reset/reinitialize the
// widget and obtain a fresh token before allowing another attempt -- never
// a silent retry re-using the same already-rejected token. Discarding
// `widgetId` here (rather than only calling `turnstile.reset`) makes the
// next getTurnstileToken() call render a brand-new widget instance, which
// is the most literal reading of "reinitialize" and needs no assumption
// about exactly how Cloudflare's own reset() re-triggers execution --
// something this sandbox has no live site to verify against.
export function resetTurnstileWidget(): void {
  if (widgetId !== null && window.turnstile) {
    window.turnstile.remove(widgetId);
  }
  widgetId = null;
}
