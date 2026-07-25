import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TurnstileApi } from './turnstile';

// REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037 (amended
// 2026-07-25 for the visible-checkbox switch): no live Cloudflare site key
// exists in this sandbox, so these tests never let a real script load
// happen -- they drive the same script.onload/render/callback contract a
// real browser + Cloudflare's script would trigger, via a fake
// `window.turnstile`.
function createFakeTurnstileApi(): TurnstileApi {
  return {
    render: vi.fn(),
    reset: vi.fn(),
    remove: vi.fn(),
  };
}

// getTurnstileToken() is an `async function` that `await`s the script-load
// promise before calling `render()` — flushing a macrotask lets that
// continuation run before a test asserts on it, the same way a real browser
// event loop would before the next line of test code runs.
function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('turnstile', () => {
  let container: HTMLDivElement;

  beforeEach(() => {
    vi.resetModules();
    document.body.innerHTML = '';
    document.head.querySelectorAll('script').forEach((node) => node.remove());
    delete (window as { turnstile?: unknown }).turnstile;
    // Sign-in latency fix (2026-07-25): getTurnstileToken() no longer owns a
    // single hidden body-level container -- callers (AuthScreen.tsx/
    // DeleteAccountScreen.tsx) supply their own, so tests do the same.
    container = document.createElement('div');
    document.body.appendChild(container);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('loads the Cloudflare script exactly once, renders a visible (normal-size) widget into the given container, and resolves with the token its callback receives', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const appendChildSpy = vi.spyOn(document.head, 'appendChild');
    const fakeApi = createFakeTurnstileApi();

    const tokenPromise = getTurnstileToken(container);

    expect(appendChildSpy).toHaveBeenCalledTimes(1);
    const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;
    expect(scriptEl.src).toBe('https://challenges.cloudflare.com/turnstile/v0/api.js');

    // Simulate the script finishing loading.
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;
    scriptEl.onload?.(new Event('load'));
    await flush();

    expect(fakeApi.render).toHaveBeenCalledTimes(1);
    const [renderedContainer, options] = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(renderedContainer).toBe(container);
    expect(options.size).toBe('normal');

    options.callback('a-real-token');
    await expect(tokenPromise).resolves.toBe('a-real-token');
  });

  it('reuses the already-loaded script on a second call rather than injecting it twice', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await expect(first).resolves.toBe('token-1');

    const appendChildSpy = vi.spyOn(document.head, 'appendChild');
    const second = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('token-2');
    await expect(second).resolves.toBe('token-2');

    expect(appendChildSpy).not.toHaveBeenCalled();
  });

  it('tears down a previous widget before rendering a fresh one on a second getTurnstileToken() call', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await first;

    const second = getTurnstileToken(container);
    await flush();
    expect(fakeApi.remove).toHaveBeenCalledWith('widget-1');
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('token-2');
    await expect(second).resolves.toBe('token-2');
  });

  // Sign-in latency fix (2026-07-25): a caller (e.g. AuthScreen.tsx's
  // handleSubmit for signup) may legitimately render into a *different*
  // container on a later, non-overlapping call (its own guest-flow
  // container vs. its form container) -- confirms the teardown/render
  // sequence works the same way regardless of whether the container
  // changed between calls.
  it('tears down a previous widget and renders into a new container when a later call supplies a different one', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const otherContainer = document.createElement('div');
    document.body.appendChild(otherContainer);
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await first;

    const second = getTurnstileToken(otherContainer);
    await flush();
    expect(fakeApi.remove).toHaveBeenCalledWith('widget-1');
    const [renderedContainer] = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1];
    expect(renderedContainer).toBe(otherContainer);
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('token-2');
    await expect(second).resolves.toBe('token-2');
  });

  // REQ-717's explicit acceptance criterion: a captcha rejection resets the
  // widget so the *next* getTurnstileToken() call is guaranteed a fresh
  // render, never a silent reuse of the rejected widget/token.
  it('resetTurnstileWidget removes the current widget so the next getTurnstileToken() call renders a brand-new one', async () => {
    const { getTurnstileToken, resetTurnstileWidget } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('rejected-token');
    await first;

    resetTurnstileWidget();
    expect(fakeApi.remove).toHaveBeenCalledWith('widget-1');

    const second = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('fresh-token');
    await expect(second).resolves.toBe('fresh-token');
    expect(fakeApi.render).toHaveBeenCalledTimes(2);
  });

  it('rejects if the script fails to load', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const appendChildSpy = vi.spyOn(document.head, 'appendChild');

    const tokenPromise = getTurnstileToken(container);
    const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;
    scriptEl.onerror?.(new Event('error'));

    await expect(tokenPromise).rejects.toThrow('Failed to load the Turnstile verification script.');
  });

  it('dedupes concurrent getTurnstileToken() calls to the same in-flight render, rather than racing them', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken(container);
    const second = getTurnstileToken(container);
    await flush();

    expect(fakeApi.render).toHaveBeenCalledTimes(1);
    expect(fakeApi.remove).not.toHaveBeenCalled();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('shared-token');

    await expect(first).resolves.toBe('shared-token');
    await expect(second).resolves.toBe('shared-token');
  });

  // Once the in-flight call above has settled, a later, non-overlapping call
  // must still get a genuinely fresh widget (the existing "tears down a
  // previous widget" test covers the sequential case; this confirms
  // dedup doesn't leak across settled calls).
  it('renders a fresh widget for a later call once the previous, deduped call has settled', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken(container);
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await first;

    const second = getTurnstileToken(container);
    await flush();
    expect(fakeApi.render).toHaveBeenCalledTimes(2);
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('token-2');
    await expect(second).resolves.toBe('token-2');
  });

  it('rejects if Turnstile itself reports an error via error-callback', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const tokenPromise = getTurnstileToken(container);
    await flush();
    const options = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1];
    options['error-callback']();

    await expect(tokenPromise).rejects.toThrow('Could not verify you are not a bot. Please try again.');
  });

  // Sign-in latency fix (2026-07-25): preloadTurnstileScript() must start
  // the script download without rendering any widget or mint any token --
  // that's the whole point of moving it earlier than submit time.
  describe('preloadTurnstileScript', () => {
    it('downloads the script but never calls render()', async () => {
      const { preloadTurnstileScript } = await import('./turnstile');
      const appendChildSpy = vi.spyOn(document.head, 'appendChild');
      const fakeApi = createFakeTurnstileApi();

      preloadTurnstileScript();

      expect(appendChildSpy).toHaveBeenCalledTimes(1);
      const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;

      (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;
      scriptEl.onload?.(new Event('load'));
      await flush();

      expect(fakeApi.render).not.toHaveBeenCalled();
    });

    it('a later getTurnstileToken() call reuses the preloaded script rather than injecting a second one', async () => {
      const { preloadTurnstileScript, getTurnstileToken } = await import('./turnstile');
      const appendChildSpy = vi.spyOn(document.head, 'appendChild');
      const fakeApi = createFakeTurnstileApi();

      preloadTurnstileScript();
      const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;
      (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;
      scriptEl.onload?.(new Event('load'));
      await flush();

      const tokenPromise = getTurnstileToken(container);
      await flush();

      expect(appendChildSpy).toHaveBeenCalledTimes(1);
      expect(fakeApi.render).toHaveBeenCalledTimes(1);
      (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
      await expect(tokenPromise).resolves.toBe('token-1');
    });

    it('does not surface a script-load failure to any caller -- getTurnstileToken() retries and surfaces it there instead', async () => {
      const { preloadTurnstileScript } = await import('./turnstile');
      const appendChildSpy = vi.spyOn(document.head, 'appendChild');

      // Should not throw or produce an unhandled rejection.
      preloadTurnstileScript();
      const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;
      scriptEl.onerror?.(new Event('error'));
      await flush();
    });
  });
});
