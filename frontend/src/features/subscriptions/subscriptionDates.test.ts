import { describe, expect, it } from 'vitest';
import { limaInput, limaStart, planPeriodEnd } from './subscriptionDates';
describe('Manual plan calendar previews and Lima input', () => {
  it.each([
    ['2026-10-07T09:30', '2026-10-07T14:30:00.000Z'],
    ['2026-10-07T09:30:42', '2026-10-07T14:30:42.000Z'],
    ['2024-02-29T23:59:59', '2024-03-01T04:59:59.000Z'],
    ['2026-12-31T23:59', '2027-01-01T04:59:00.000Z'],
  ])('converts authorized Lima input %s to the correct UTC instant', (input, utc) => {
    expect(limaStart(input)).toBe(utc);
    expect(limaInput(utc)).toBe(input.length === 16 ? input + ':00' : input);
  });
  it.each(['', '2025-02-29T09:30', '2026-04-31T09:30', '2026-13-01T09:30', '2026-10-07T24:01', '2026-10-07T09:60', '2026-10-07T09:30Z', '0000-10-07T09:30', '2026-1-07T09:30', '2026-10-07T09:30\n'])('rejects impossible or ambiguous input %s', (input) => {
    expect(limaStart(input)).toBeNull();
  });
  it.each([
    ['2026-10-07T14:30:00Z', '2027-01-07T14:30:00.000Z'],
    ['2026-01-31T14:30:00Z', '2026-04-30T14:30:00.000Z'],
    ['2023-11-30T14:30:00Z', '2024-02-29T14:30:00.000Z'],
    ['2024-11-30T14:30:00Z', '2025-02-28T14:30:00.000Z'],
  ])('previews three natural months from %s without a fixed-day duration', (start, end) => {
    expect(planPeriodEnd('probationismo', start)).toBe(end);
  });
  it.each([
    ['2026-10-07T14:30:00Z', '2027-10-07T14:30:00.000Z'],
    ['2024-02-29T14:30:00Z', '2025-02-28T14:30:00.000Z'],
  ])('previews a natural year from %s including leap-day clamping', (start, end) => {
    expect(planPeriodEnd('annual', start)).toBe(end);
  });
  it('has no fabricated free expiry and rejects calendar overflow or an invalid start', () => {
    expect(planPeriodEnd('free_beta', '2026-10-07T14:30:00Z')).toBeNull();
    expect(planPeriodEnd('annual', '9999-10-07T14:30:00Z')).toBeNull();
    expect(planPeriodEnd('probationismo', 'bad')).toBeNull();
  });
});
