import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TurnstileApi } from './turnstile';

// REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: no live
// Cloudflare site key exists in this sandbox, so these tests never let a
// real script load happen -- they drive the same script.onload/render/
// callback contract a real browser + Cloudflare's script would trigger,
// via a fake `window.turnstile`.
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
  beforeEach(() => {
    vi.resetModules();
    document.body.innerHTML = '';
    document.head.querySelectorAll('script').forEach((node) => node.remove());
    delete (window as { turnstile?: unknown }).turnstile;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('loads the Cloudflare script exactly once, renders an invisible widget, and resolves with the token its callback receives', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const appendChildSpy = vi.spyOn(document.head, 'appendChild');
    const fakeApi = createFakeTurnstileApi();

    const tokenPromise = getTurnstileToken();

    expect(appendChildSpy).toHaveBeenCalledTimes(1);
    const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;
    expect(scriptEl.src).toBe('https://challenges.cloudflare.com/turnstile/v0/api.js');

    // Simulate the script finishing loading.
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;
    scriptEl.onload?.(new Event('load'));
    await flush();

    expect(fakeApi.render).toHaveBeenCalledTimes(1);
    const options = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1];
    expect(options.size).toBe('invisible');

    options.callback('a-real-token');
    await expect(tokenPromise).resolves.toBe('a-real-token');
  });

  it('reuses the already-loaded script on a second call rather than injecting it twice', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken();
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await expect(first).resolves.toBe('token-1');

    const appendChildSpy = vi.spyOn(document.head, 'appendChild');
    const second = getTurnstileToken();
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

    const first = getTurnstileToken();
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await first;

    const second = getTurnstileToken();
    await flush();
    expect(fakeApi.remove).toHaveBeenCalledWith('widget-1');
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

    const first = getTurnstileToken();
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('rejected-token');
    await first;

    resetTurnstileWidget();
    expect(fakeApi.remove).toHaveBeenCalledWith('widget-1');

    const second = getTurnstileToken();
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('fresh-token');
    await expect(second).resolves.toBe('fresh-token');
    expect(fakeApi.render).toHaveBeenCalledTimes(2);
  });

  it('rejects if the script fails to load', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const appendChildSpy = vi.spyOn(document.head, 'appendChild');

    const tokenPromise = getTurnstileToken();
    const scriptEl = appendChildSpy.mock.calls[0]?.[0] as HTMLScriptElement;
    scriptEl.onerror?.(new Event('error'));

    await expect(tokenPromise).rejects.toThrow('Failed to load the Turnstile verification script.');
  });

  // Gap check (test-writer): getOrCreateContainer() looks up the container by
  // its fixed id before ever creating one -- this asserts that holds across
  // repeated getTurnstileToken() calls, so a real guest-play retry loop never
  // leaks a second hidden <div> into the DOM.
  it('REQ-717: reuses the same container element in the DOM across repeated getTurnstileToken() calls, never leaking a new one', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (fakeApi.render as ReturnType<typeof vi.fn>).mockReturnValueOnce('widget-1').mockReturnValueOnce('widget-2');
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken();
    await flush();
    const firstContainer = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][0];
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await first;

    expect(document.querySelectorAll('#turnstile-widget-container')).toHaveLength(1);

    const second = getTurnstileToken();
    await flush();
    const secondContainer = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][0];
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('token-2');
    await second;

    expect(document.querySelectorAll('#turnstile-widget-container')).toHaveLength(1);
    expect(secondContainer).toBe(firstContainer);
  });

  // Regression test (quality-architect): a caller invoking getTurnstileToken()
  // again before a previous call has settled used to have its widget torn
  // down mid-flight by the second call's own teardown-then-render logic,
  // leaving the first call's promise permanently unresolved (never resolved
  // nor rejected). Both calls must now resolve to the same token, from a
  // single render().
  it('dedupes concurrent getTurnstileToken() calls to the same in-flight render, rather than racing them', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const first = getTurnstileToken();
    const second = getTurnstileToken();
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

    const first = getTurnstileToken();
    await flush();
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1].callback('token-1');
    await first;

    const second = getTurnstileToken();
    await flush();
    expect(fakeApi.render).toHaveBeenCalledTimes(2);
    (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[1][1].callback('token-2');
    await expect(second).resolves.toBe('token-2');
  });

  it('rejects if Turnstile itself reports an error via error-callback', async () => {
    const { getTurnstileToken } = await import('./turnstile');
    const fakeApi = createFakeTurnstileApi();
    (window as { turnstile?: TurnstileApi }).turnstile = fakeApi;

    const tokenPromise = getTurnstileToken();
    await flush();
    const options = (fakeApi.render as ReturnType<typeof vi.fn>).mock.calls[0][1];
    options['error-callback']();

    await expect(tokenPromise).rejects.toThrow('Could not verify you are not a bot. Please try again.');
  });
});
