import { describe, expect, it } from 'vitest';
import { shortUserId } from './shortUserId';

// REQ-1401/1402 (S-217): design-document.md SCREEN-15's own "Identity gap"
// note — this is the deliberately short, stable, deterministic stand-in for
// a real display name until a backend endpoint exists to resolve one.
describe('shortUserId', () => {
  it('REQ-1401: renders "Player " followed by the first 8 characters of the id, uppercased', () => {
    expect(shortUserId('a1b2c3d4-e5f6-7890-abcd-ef1234567890')).toBe('Player A1B2C3D4');
  });

  it('REQ-1401: is deterministic — the same id always produces the same label', () => {
    const id = '11223344-5566-7788-99aa-bbccddeeff00';
    expect(shortUserId(id)).toBe(shortUserId(id));
  });

  it('REQ-1401: two different ids produce different labels', () => {
    expect(shortUserId('aaaaaaaa-0000-0000-0000-000000000000')).not.toBe(
      shortUserId('bbbbbbbb-0000-0000-0000-000000000000'),
    );
  });
});
