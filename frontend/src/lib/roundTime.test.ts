import { describe, expect, it } from 'vitest';
import { formatRoundEndTime, formatRoundEndTimeAccessibleLabel } from './roundTime';

// REQ-303 (2026-07-21 addition): the grid header's round end-time indicator.
// A fixed reference "now" so every bucket boundary below is computed against
// a known, stable offset rather than the real clock.
const REFERENCE_TIME = new Date('2026-07-21T12:00:00.000Z');

function endTimeOffsetByMs(ms: number): string {
  return new Date(REFERENCE_TIME.getTime() + ms).toISOString();
}

const MS_PER_MINUTE = 60_000;
const MS_PER_HOUR = 60 * MS_PER_MINUTE;
const MS_PER_DAY = 24 * MS_PER_HOUR;

describe('formatRoundEndTime', () => {
  describe('24 hours or more remaining', () => {
    it('REQ-303: formats as "Ends in {D}d {H}h" when both days and a partial-hour remainder are present', () => {
      const endTimeIso = endTimeOffsetByMs(2 * MS_PER_DAY + 5 * MS_PER_HOUR);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 2d 5h');
    });

    it('REQ-303: omits the hour part when it floors to 0 (exactly 2 days remaining reads "Ends in 2d", not "Ends in 2d 0h")', () => {
      const endTimeIso = endTimeOffsetByMs(2 * MS_PER_DAY);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 2d');
    });

    it('REQ-303: floors rather than rounds up — 2d 23h 59m remaining reads "Ends in 2d 23h", never "Ends in 3d"', () => {
      const endTimeIso = endTimeOffsetByMs(2 * MS_PER_DAY + 23 * MS_PER_HOUR + 59 * MS_PER_MINUTE);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 2d 23h');
    });
  });

  describe('between 1 and 24 hours remaining', () => {
    it('REQ-303: formats as "Ends in {H}h {M}m" when both hours and a partial-minute remainder are present', () => {
      const endTimeIso = endTimeOffsetByMs(3 * MS_PER_HOUR + 30 * MS_PER_MINUTE);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 3h 30m');
    });

    it('REQ-303: omits the minute part when it floors to 0 (exactly 3 hours remaining reads "Ends in 3h", not "Ends in 3h 0m")', () => {
      const endTimeIso = endTimeOffsetByMs(3 * MS_PER_HOUR);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 3h');
    });

    it('REQ-303: floors rather than rounds up — 23h 59m remaining reads "Ends in 23h 59m", never "Ends in 24h"', () => {
      const endTimeIso = endTimeOffsetByMs(23 * MS_PER_HOUR + 59 * MS_PER_MINUTE);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 23h 59m');
    });
  });

  describe('between 1 minute and 1 hour remaining', () => {
    it('REQ-303: formats as "Ends in {M}m"', () => {
      const endTimeIso = endTimeOffsetByMs(45 * MS_PER_MINUTE);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 45m');
    });

    it('REQ-303: floors rather than rounds up — 59.9 minutes remaining reads "Ends in 59m", never "Ends in 1h"', () => {
      const endTimeIso = endTimeOffsetByMs(59 * MS_PER_MINUTE + 59_000);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 59m');
    });

    it('REQ-303: exactly 1 minute remaining still reads "Ends in 1m", not "Ending soon"', () => {
      const endTimeIso = endTimeOffsetByMs(MS_PER_MINUTE);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ends in 1m');
    });
  });

  describe('"Ending soon" fallback', () => {
    it('REQ-303: under 60 seconds remaining reads "Ending soon", never "0m" or a fractional value', () => {
      const endTimeIso = endTimeOffsetByMs(30_000);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ending soon');
    });

    it('REQ-303: an endTime already in the past (negative remaining — clock skew, or round-close job hasn\'t run yet) also reads "Ending soon", never a negative duration', () => {
      const endTimeIso = endTimeOffsetByMs(-10 * MS_PER_MINUTE);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ending soon');
    });

    it('REQ-303: an endTime exactly equal to referenceTime (0 remaining) reads "Ending soon"', () => {
      const endTimeIso = endTimeOffsetByMs(0);
      expect(formatRoundEndTime(endTimeIso, REFERENCE_TIME).text).toBe('Ending soon');
    });
  });

  describe('absoluteLabel', () => {
    it('REQ-303: is always populated, independent of which text bucket applies', () => {
      const buckets = [
        endTimeOffsetByMs(2 * MS_PER_DAY + 5 * MS_PER_HOUR),
        endTimeOffsetByMs(3 * MS_PER_HOUR + 30 * MS_PER_MINUTE),
        endTimeOffsetByMs(45 * MS_PER_MINUTE),
        endTimeOffsetByMs(-10 * MS_PER_MINUTE),
      ];

      for (const endTimeIso of buckets) {
        const { absoluteLabel } = formatRoundEndTime(endTimeIso, REFERENCE_TIME);
        expect(absoluteLabel).toBeTruthy();
        expect(absoluteLabel.length).toBeGreaterThan(0);
      }
    });

    it('REQ-303: reflects the actual endTime, not the text bucket — a fixed endTime yields the same absoluteLabel from two different reference times that land in different text buckets', () => {
      const fixedEndTime = '2026-08-01T09:30:00.000Z';

      const fromFarAway = formatRoundEndTime(fixedEndTime, new Date('2026-07-25T09:30:00.000Z')); // ~7 days out
      const fromCloseUp = formatRoundEndTime(fixedEndTime, new Date('2026-08-01T09:00:00.000Z')); // 30 minutes out

      expect(fromFarAway.text).not.toBe(fromCloseUp.text);
      expect(fromFarAway.absoluteLabel).toBe(fromCloseUp.absoluteLabel);
      expect(fromFarAway.absoluteLabel).toBe(new Date(fixedEndTime).toLocaleString(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }));
    });
  });
});

describe('formatRoundEndTimeAccessibleLabel', () => {
  it('REQ-303: combines the relative text and the absolute label into one accessible-name string', () => {
    const display = { text: 'Ends in 2d 5h', absoluteLabel: 'Aug 1, 2026, 9:30 AM' };
    expect(formatRoundEndTimeAccessibleLabel(display)).toBe(
      'Ends in 2d 5h. Round ends Aug 1, 2026, 9:30 AM.',
    );
  });

  it('REQ-303: still produces a meaningful accessible name when text is the "Ending soon" fallback', () => {
    const display = { text: 'Ending soon', absoluteLabel: 'Aug 1, 2026, 9:30 AM' };
    expect(formatRoundEndTimeAccessibleLabel(display)).toBe(
      'Ending soon. Round ends Aug 1, 2026, 9:30 AM.',
    );
  });
});
