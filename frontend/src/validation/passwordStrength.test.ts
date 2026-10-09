import { afterEach, describe, expect, it, vi } from 'vitest';
import { estimatePasswordStrength } from './passwordStrength';

const failureMessage = 'No pudimos estimar la fortaleza de la contraseña.';

describe('Local password strength estimation with the real dictionaries', () => {
  it.each([
    'aaaaaaaaaaaaaaaaaaaa',
    '01234567890123456789',
    'qwertyuiopasdfghjkl',
    'PasswordPassword1!',
  ])(
    'identifies a predictable pattern despite its length and character classes',
    async (password) => {
      expect((await estimatePasswordStrength(password))?.score).toBeLessThanOrEqual(2);
    },
  );

  it.each(['understanding', 'comprensión'])(
    'recognizes common words in both languages',
    async (password) => {
      expect((await estimatePasswordStrength(password))?.score).toBeLessThanOrEqual(1);
    },
  );

  it('supports a long phrase without requiring digits, uppercase or symbols', async () => {
    const result = await estimatePasswordStrength('farola glacial cuaderno océano zafiro');
    expect(result?.score).toBe(4);
  });

  it('retains the separate ranked dictionaries for both English and Spanish', async () => {
    const [common, english, spanish] = await Promise.all([
      import('@zxcvbn-ts/language-common'),
      import('@zxcvbn-ts/language-en'),
      import('@zxcvbn-ts/language-es-es'),
    ]);
    const keys = [
      ...Object.keys(common.dictionary),
      ...Object.keys(english.dictionary),
      ...Object.keys(spanish.dictionary),
    ];
    expect(new Set(keys).size).toBe(keys.length);
    expect(english.dictionary).toHaveProperty('commonWords-en');
    expect(spanish.dictionary).toHaveProperty('commonWords-es-es');
  });

  it('returns only the score and translated feedback, without the password or sequence', async () => {
    const result = await estimatePasswordStrength('aaaaaaaaaaaaaaaaaaaa');
    expect(Object.keys(result ?? {}).sort()).toEqual(['score', 'suggestions', 'warning']);
    expect(result?.warning).not.toBe('');
    expect(JSON.stringify(result)).not.toContain('aaaaaaaaaaaaaaaaaaaa');
    expect(JSON.stringify(result)).not.toMatch(/"password"|"sequence"/);
  });

  it('skips empty and oversized input rather than truncating it for estimation', async () => {
    expect(await estimatePasswordStrength('')).toBeNull();
    expect(await estimatePasswordStrength('a'.repeat(129))).toBeNull();
    expect(await estimatePasswordStrength('😀'.repeat(65))).toBeNull();
    expect((await estimatePasswordStrength('a'.repeat(128)))?.score).toBeLessThanOrEqual(1);
  });
});

describe('Estimator loading and safe result boundary', () => {
  afterEach(() => {
    vi.doUnmock('@zxcvbn-ts/core');
    vi.doUnmock('@zxcvbn-ts/language-common');
    vi.doUnmock('@zxcvbn-ts/language-en');
    vi.doUnmock('@zxcvbn-ts/language-es-es');
    vi.resetModules();
  });

  function mockEstimator(check: ReturnType<typeof vi.fn>) {
    const factory = vi.fn(function () {
      return { check };
    });
    vi.doMock('@zxcvbn-ts/core', () => ({ ZxcvbnFactory: factory }));
    vi.doMock('@zxcvbn-ts/language-common', () => ({
      dictionary: { passwords: ['common'] },
      adjacencyGraphs: {},
    }));
    vi.doMock('@zxcvbn-ts/language-en', () => ({ dictionary: { english: ['word'] } }));
    vi.doMock('@zxcvbn-ts/language-es-es', () => ({
      dictionary: { spanish: ['palabra'] },
      translations: {
        warnings: { common: 'Evita patrones repetidos.' },
        suggestions: { words: 'Añade palabras poco relacionadas.' },
      },
    }));
    return factory;
  }

  it('loads once on demand and keeps password and personal context out of shared options', async () => {
    vi.resetModules();
    const check = vi.fn().mockReturnValue({
      score: 3,
      password: 'private estimator value',
      sequence: ['private matching details'],
      feedback: {
        warning: 'Evita patrones repetidos.',
        suggestions: ['Añade palabras poco relacionadas.', 'private estimator value'],
      },
    });
    const factory = mockEstimator(check);
    const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
    expect(factory).not.toHaveBeenCalled();
    expect(await estimate('')).toBeNull();
    expect(await estimate('a'.repeat(129))).toBeNull();
    expect(factory).not.toHaveBeenCalled();
    const [first, second] = await Promise.all([
      estimate('  clave exacta con espacios  ', ['Persona', 'persona@example.test']),
      estimate('otra frase completamente distinta'),
    ]);
    expect(factory).toHaveBeenCalledOnce();
    expect(factory).toHaveBeenCalledWith({
      dictionary: { passwords: ['common'], english: ['word'], spanish: ['palabra'] },
      graphs: {},
      translations: {
        warnings: { common: 'Evita patrones repetidos.' },
        suggestions: { words: 'Añade palabras poco relacionadas.' },
      },
      maxLength: 128,
    });
    expect(check).toHaveBeenCalledWith('  clave exacta con espacios  ', [
      'Persona',
      'persona@example.test',
    ]);
    expect(check).toHaveBeenCalledWith('otra frase completamente distinta', []);
    expect(first).toEqual({
      score: 3,
      warning: 'Evita patrones repetidos.',
      suggestions: ['Añade palabras poco relacionadas.'],
    });
    expect(second).toEqual(first);
  });

  it('filters any feedback that was not supplied by the local Spanish translations', async () => {
    vi.resetModules();
    mockEstimator(
      vi.fn().mockReturnValue({
        score: 2,
        feedback: { warning: 'private input echoed by estimator', suggestions: ['private detail'] },
      }),
    );
    const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
    expect(await estimate('una contraseña suficientemente larga')).toEqual({
      score: 2,
      warning: '',
      suggestions: [],
    });
  });

  it('normalizes an absent warning to an empty safe string', async () => {
    vi.resetModules();
    mockEstimator(
      vi.fn().mockReturnValue({ score: 4, feedback: { warning: null, suggestions: [] } }),
    );
    const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
    expect(await estimate('una contraseña suficientemente larga')).toEqual({
      score: 4,
      warning: '',
      suggestions: [],
    });
  });

  it('allows a subsequent retry after a generic estimator initialization failure', async () => {
    vi.resetModules();
    const check = vi.fn().mockReturnValue({
      score: 4,
      feedback: { warning: '', suggestions: [] },
    });
    const factory = mockEstimator(check);
    factory.mockImplementationOnce(function () {
      throw new Error('private initialization diagnostic');
    });
    const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
    await expect(estimate('una contraseña suficientemente larga')).rejects.toThrow(failureMessage);
    expect(factory).toHaveBeenCalledOnce();
    expect((await estimate('una contraseña suficientemente larga'))?.score).toBe(4);
    expect(factory).toHaveBeenCalledTimes(2);
  });

  it('sanitizes a failed dynamic dictionary import without creating an estimator', async () => {
    vi.resetModules();
    const factory = mockEstimator(vi.fn());
    vi.doMock('@zxcvbn-ts/language-es-es', () => {
      throw new Error('private loading diagnostic');
    });
    const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
    await expect(estimate('una contraseña suficientemente larga')).rejects.toThrow(failureMessage);
    expect(factory).not.toHaveBeenCalled();
  });

  it.each([-1, 5, 2.5, Number.NaN])(
    'rejects an invalid estimator score with a generic failure',
    async (score) => {
      vi.resetModules();
      mockEstimator(vi.fn().mockReturnValue({ score, feedback: { warning: '', suggestions: [] } }));
      const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
      await expect(estimate('una contraseña suficientemente larga')).rejects.toThrow(
        failureMessage,
      );
    },
  );

  it('sanitizes estimator errors rather than exposing diagnostics', async () => {
    vi.resetModules();
    mockEstimator(
      vi.fn().mockImplementation(() => {
        throw new Error('private password matching diagnostic');
      }),
    );
    const { estimatePasswordStrength: estimate } = await import('./passwordStrength');
    await expect(estimate('una contraseña suficientemente larga')).rejects.toThrow(failureMessage);
  });
});
