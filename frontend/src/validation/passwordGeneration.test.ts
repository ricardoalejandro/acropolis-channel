import { describe, expect, it, vi } from 'vitest';
import { generatePassword } from './passwordGeneration';

const validCandidate = [0, 26, 52, 62, ...Array<number>(16).fill(1)];
const failureMessage = 'No pudimos generar una contraseña. Inténtalo de nuevo.';

function randomStream(stream: number[], fallback = 0) {
  let position = 0;
  const getRandomValues = vi.fn((bytes: Uint8Array) => {
    for (let index = 0; index < bytes.length; index += 1)
      bytes[index] = stream[position++] ?? fallback;
    return bytes;
  });
  vi.stubGlobal('crypto', { getRandomValues });
  return getRandomValues;
}

describe('Cryptographic password generation', () => {
  it('generates exactly 20 characters with all four requested classes', () => {
    const random = randomStream(validCandidate);
    const result = generatePassword();
    expect(result).toBe('Aa0!' + 'B'.repeat(16));
    expect(result).toHaveLength(20);
    expect(result).toMatch(/[A-Z]/);
    expect(result).toMatch(/[a-z]/);
    expect(result).toMatch(/\d/);
    expect(result).toMatch(/[!@#$%^&*()\-_=+?]/);
    expect(random).toHaveBeenCalledOnce();
  });

  it('discards bytes from the biased remainder and accepts the last unbiased byte', () => {
    randomStream([231, 255, 230, ...validCandidate.slice(0, 19)]);
    expect(generatePassword()).toBe('?Aa0!' + 'B'.repeat(15));
  });

  it('discards an entire candidate when a requested character class is missing', () => {
    randomStream([...Array<number>(20).fill(0), ...validCandidate]);
    expect(generatePassword()).toBe('Aa0!' + 'B'.repeat(16));
  });

  it('bounds candidate attempts for a random source that produces only one class', () => {
    const random = randomStream([]);
    expect(generatePassword).toThrow(failureMessage);
    expect(random.mock.calls.length).toBeLessThanOrEqual(64);
  });

  it('bounds byte consumption when every byte is rejected', () => {
    const random = randomStream([], 255);
    expect(generatePassword).toThrow(failureMessage);
    expect(random).toHaveBeenCalledTimes(128);
  });

  it('does not substitute insecure randomness when Web Crypto is unavailable', () => {
    vi.stubGlobal('crypto', undefined);
    const insecure = vi.spyOn(Math, 'random');
    expect(generatePassword).toThrow(failureMessage);
    expect(insecure).not.toHaveBeenCalled();
  });

  it('returns a generic failure without exposing a random source diagnostic', () => {
    vi.stubGlobal('crypto', {
      getRandomValues: () => {
        throw new Error('private random source diagnostic');
      },
    });
    expect(generatePassword).toThrow(failureMessage);
  });
});
