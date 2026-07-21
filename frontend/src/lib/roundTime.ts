// REQ-303 (2026-07-21 addition): formats the grid header's round end-time
// indicator (`GridScreen`'s `grid-screen__end-time` element, next to the
// `(ⓘ)` scoring-explainer entry point). A small, pure helper on purpose —
// `GridScreen` calls this exactly once, at the moment `GET /rounds/current`
// resolves, using the client's clock at that instant as `referenceTime`; it
// is NOT re-invoked on every render or on a timer. That's a deliberate
// Tier 0 simplification (no periodic-tick/live-countdown requirement) — see
// requirements-document.md REQ-303's 2026-07-21 addition for the full
// rationale. The relative text only reflects reality again on the next
// fetch (e.g. a page reload), and that's fine.
export interface RoundEndTimeDisplay {
  // Visible relative-duration text, floored (never rounded up) to whole
  // units, or the fixed "Ending soon" label — never a negative, zero, or
  // otherwise nonsensical value.
  text: string;
  // The exact end date/time in the viewer's own local timezone (via
  // Intl/toLocaleString, so it's always the *viewer's* zone, never a fixed
  // one) — always populated, independent of `text`, so a screen-reader or
  // keyboard user always has the absolute time available even when `text`
  // reads "Ending soon" and the relative duration alone would say nothing
  // useful.
  absoluteLabel: string;
}

const MINUTE_MS = 60_000;
const HOUR_MINUTES = 60;
const DAY_MINUTES = 24 * HOUR_MINUTES;
// Under a minute remaining (or already in the past — clock skew, or the
// round-close job hasn't run yet) reads as "Ending soon" rather than a
// computed duration that could otherwise read as "0m" or negative.
const ENDING_SOON_THRESHOLD_MS = MINUTE_MS;

export function formatRoundEndTime(endTimeIso: string, referenceTime: Date): RoundEndTimeDisplay {
  const endTime = new Date(endTimeIso);
  const remainingMs = endTime.getTime() - referenceTime.getTime();

  // A malformed/unparseable `endTime` (the API contract guarantees a valid
  // ISO string, but this is a pure formatter with no other guard between it
  // and that contract) must not be allowed to leak `NaN`/`Invalid Date` into
  // either the visible text or the accessible name — `NaN` comparisons are
  // always false, so without this explicit check it would fall all the way
  // through to the `Ends in {M}m` branch as `"Ends in NaNm"`, which is
  // exactly the "nonsensical value" REQ-303 rules out.
  if (Number.isNaN(remainingMs)) {
    return { text: 'Ending soon', absoluteLabel: 'an unknown time' };
  }

  const absoluteLabel = endTime.toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  });

  if (remainingMs < ENDING_SOON_THRESHOLD_MS) {
    return { text: 'Ending soon', absoluteLabel };
  }

  const totalMinutes = Math.floor(remainingMs / MINUTE_MS);

  // 24 hours or more remaining: "{D}d {H}h" (hour part omitted if it floors
  // to 0).
  if (totalMinutes >= DAY_MINUTES) {
    const days = Math.floor(totalMinutes / DAY_MINUTES);
    const hours = Math.floor((totalMinutes % DAY_MINUTES) / HOUR_MINUTES);
    return { text: hours > 0 ? `Ends in ${days}d ${hours}h` : `Ends in ${days}d`, absoluteLabel };
  }

  // Between 1 and 24 hours remaining: "{H}h {M}m" (minute part omitted if it
  // floors to 0).
  if (totalMinutes >= HOUR_MINUTES) {
    const hours = Math.floor(totalMinutes / HOUR_MINUTES);
    const minutes = totalMinutes % HOUR_MINUTES;
    return { text: minutes > 0 ? `Ends in ${hours}h ${minutes}m` : `Ends in ${hours}h`, absoluteLabel };
  }

  // Between 1 minute and 1 hour remaining: "{M}m". totalMinutes is
  // guaranteed >= 1 here since remainingMs already cleared the
  // ENDING_SOON_THRESHOLD_MS check above.
  return { text: `Ends in ${totalMinutes}m`, absoluteLabel };
}

// The indicator's full accessible name — pairs the visible relative text
// with the absolute local date/time so the absolute time is exposed via the
// accessible name itself (not a hover-only tooltip), per REQ-303.
export function formatRoundEndTimeAccessibleLabel(display: RoundEndTimeDisplay): string {
  return `${display.text}. Round ends ${display.absoluteLabel}.`;
}
