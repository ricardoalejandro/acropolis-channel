import type { ZxcvbnFactory } from '@zxcvbn-ts/core';

export type PasswordStrength = {
  score: 0 | 1 | 2 | 3 | 4;
  warning: string;
  suggestions: string[];
};

type LoadedEstimator = { estimator: ZxcvbnFactory; feedback: Set<string> };
const FAILURE_MESSAGE = 'No pudimos estimar la fortaleza de la contraseña.';
let estimatorPromise: Promise<LoadedEstimator> | undefined;

function feedbackStrings(value: unknown): string[] {
  if (typeof value === 'string') return [value];
  if (value && typeof value === 'object') return Object.values(value).flatMap(feedbackStrings);
  return [];
}

function loadEstimator(): Promise<LoadedEstimator> {
  estimatorPromise ??= Promise.all([
    import('@zxcvbn-ts/core'),
    import('@zxcvbn-ts/language-common'),
    import('@zxcvbn-ts/language-en'),
    import('@zxcvbn-ts/language-es-es'),
  ])
    .then(([core, common, english, spanish]) => ({
      estimator: new core.ZxcvbnFactory({
        dictionary: { ...common.dictionary, ...english.dictionary, ...spanish.dictionary },
        graphs: common.adjacencyGraphs,
        translations: spanish.translations,
        maxLength: 128,
      }),
      feedback: new Set([
        ...feedbackStrings(spanish.translations.warnings),
        ...feedbackStrings(spanish.translations.suggestions),
      ]),
    }))
    .catch(() => {
      estimatorPromise = undefined;
      throw new Error(FAILURE_MESSAGE);
    });
  return estimatorPromise;
}

export async function estimatePasswordStrength(
  password: string,
  userInputs: readonly string[] = [],
): Promise<PasswordStrength | null> {
  if (!password.length || password.length > 128) return null;
  try {
    const { estimator, feedback } = await loadEstimator();
    const result = estimator.check(password, [...userInputs]);
    const score = result.score;
    const warning = result.feedback.warning ?? '';
    if (!Number.isInteger(score) || score < 0 || score > 4) throw new Error(FAILURE_MESSAGE);
    return {
      score: score as PasswordStrength['score'],
      warning: feedback.has(warning) ? warning : '',
      suggestions: result.feedback.suggestions.filter((suggestion) => feedback.has(suggestion)),
    };
  } catch {
    throw new Error(FAILURE_MESSAGE);
  }
}
