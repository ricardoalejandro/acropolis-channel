import { useEffect, useState } from 'react';
import { estimatePasswordStrength, type PasswordStrength } from '../validation/passwordStrength';

const levels = ['Muy débil', 'Débil', 'Moderada', 'Fuerte', 'Muy fuerte'];
export function PasswordStrengthMeter({
  id,
  value,
  userInputs,
}: {
  id: string;
  value: string;
  userInputs: readonly string[];
}) {
  const [estimate, setEstimate] = useState<{
    value: string;
    context: string;
    result: PasswordStrength | null;
    failed: boolean;
  } | null>(null);
  const context = JSON.stringify(userInputs);
  useEffect(() => {
    if (!value || value.length > 128) return;
    let current = true;
    const timer = setTimeout(() => {
      void estimatePasswordStrength(value, JSON.parse(context) as string[]).then(
        (result) => {
          if (current) setEstimate({ value, context, result, failed: false });
        },
        () => {
          if (current) setEstimate({ value, context, result: null, failed: true });
        },
      );
    }, 200);
    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [value, context]);
  const fresh = estimate?.value === value && estimate.context === context ? estimate : null;
  const result = fresh?.result;
  return (
    <div id={id} className="password-strength">
      <div aria-live="polite" aria-atomic="true">
        {!value ? (
          <p className="field-help">Fortaleza estimada: escribe una contraseña.</p>
        ) : value.length > 128 ? (
          <p className="field-help">Usa entre 15 y 128 caracteres.</p>
        ) : fresh?.failed ? (
          <p className="field-help">No pudimos estimar la fortaleza; puedes continuar.</p>
        ) : result ? (
          <>
            <p className="password-strength-label">
              Fortaleza estimada: <strong>{levels[result.score]}</strong>
            </p>
            <div
              className={'password-meter password-meter-' + result.score}
              role="meter"
              aria-label="Fortaleza estimada"
              aria-valuemin={0}
              aria-valuemax={4}
              aria-valuenow={result.score}
              aria-valuetext={levels[result.score]}
            >
              {levels.map((level, index) => (
                <span
                  key={level}
                  className={index <= result.score ? 'filled' : undefined}
                  aria-hidden="true"
                />
              ))}
            </div>
            {(result.warning || result.suggestions[0]) && (
              <p className="field-help">{result.warning || result.suggestions[0]}</p>
            )}
          </>
        ) : (
          <p className="field-help">Estimando fortaleza…</p>
        )}
      </div>
    </div>
  );
}
