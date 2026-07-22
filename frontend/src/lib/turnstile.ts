// REQ-717's 2026-07-21 "Bot-check (captcha) for guest creation" addition /
// ADR-0037: a small, promise-based wrapper around Cloudflare Turnstile's
// client-side widget/JS so AuthScreen.tsx never juggles the script tag or
// the imperative `window.turnstile` API directly, and so this module can be
// mocked wholesale in tests rather than requiring a live Cloudflare site
// (untestable in this sandbox -- see this module's own test file).
//
// Widget mode: invisible/managed, per REQ-717's explicit widget UX
// recommendation -- renders no visible UI in the common case, matching
// "Play as guest"'s zero-friction intent. If Cloudflare's own risk scoring
// ever escalates to an interactive challenge, that challenge is Cloudflare's
// own overlay/UI, not this app's -- nothing here themes it, and
// docs/design-document.md §2 needs no new token for it (checked per this
// task's own instruction before writing this file).
//
// The site key is public and safe in frontend code -- same
// `import.meta.env.VITE_*` convention `frontend/src/lib/api.ts` already uses
// for `VITE_API_BASE_URL` (ADR-0037's configuration-split decision). The
// Turnstile *secret* key never appears anywhere in this codebase (it lives
// solely in Supabase's own Auth dashboard settings) -- see ADR-0037's "For
// AI agents" section before ever adding one here.
const SITE_KEY = import.meta.env.VITE_TURNSTILE_SITE_KEY ?? '';

const SCRIPT_SRC = 'https://challenges.cloudflare.com/turnstile/v0/api.js';
const CONTAINER_ID = 'turnstile-widget-container';

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

function getOrCreateContainer(): HTMLElement {
  let container = document.getElementById(CONTAINER_ID);
  if (!container) {
    container = document.createElement('div');
    container.id = CONTAINER_ID;
    // Invisible/managed mode renders nothing visible here in the common
    // case -- hidden defensively in case Cloudflare ever leaves an empty
    // frame behind; the fallback interactive challenge (if it ever fires)
    // is Cloudflare's own overlay, unaffected by this container's display.
    container.style.display = 'none';
    document.body.appendChild(container);
  }
  return container;
}

// REQ-717: obtains one Cloudflare Turnstile token before "Play as guest"
// ever calls POST /auth/guest. Any widget instance left over from a
// previous, already-settled call is torn down first (Cloudflare's render()
// is not documented as safe to call twice into the same container without
// doing so) and a fresh one is rendered every time -- this makes every
// call, not only the one after resetTurnstileWidget(), get a genuinely new
// execution rather than relying on an assumption about how an
// already-rendered invisible widget behaves on a second callback. Never
// resolves with a placeholder/empty token; a script load failure or a
// Turnstile-reported error rejects instead.
//
// Concurrent calls (a caller invoking this again before a previous call has
// settled) reuse that same in-flight promise instead of racing it: without
// this guard, the second call's widget teardown above would remove the
// first call's still-pending widget out from under it, and the first
// call's promise would never resolve or reject at all (see pendingTokenPromise's
// own comment).
export function getTurnstileToken(): Promise<string> {
  if (pendingTokenPromise) return pendingTokenPromise;

  pendingTokenPromise = (async () => {
    try {
      const turnstile = await loadTurnstileScript();
      const container = getOrCreateContainer();

      if (widgetId !== null) {
        turnstile.remove(widgetId);
        widgetId = null;
      }

      return await new Promise<string>((resolve, reject) => {
        widgetId = turnstile.render(container, {
          sitekey: SITE_KEY,
          size: 'invisible',
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
