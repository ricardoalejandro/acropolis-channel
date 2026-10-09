import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError } from '../api/identity';
import { generatePassword } from '../validation/passwordGeneration';
import { Icon } from './Icon';
import { PasswordStrengthMeter } from './PasswordStrengthMeter';
export type Field = {
  name: string;
  label: string;
  type?: string;
  autoComplete?: string;
  help?: string;
  initial?: string;
  passwordPurpose?: 'current' | 'new' | 'confirmation';
  confirmationField?: string;
};
export function AccountForm({
  fields,
  submit,
  submitVariant = 'primary',
  onSubmit,
  children,
}: {
  fields: Field[];
  submit: string;
  submitVariant?: 'primary' | 'secondary';
  onSubmit: (values: Record<string, string>) => Promise<void>;
  children?: ReactNode;
}) {
  const id = useId();
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(fields.map((field) => [field.name, field.initial ?? ''])),
  );
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [visible, setVisible] = useState<Record<string, boolean>>({});
  const [notices, setNotices] = useState<Record<string, string>>({});
  const [copyingField, setCopyingField] = useState<string | null>(null);
  const copyRevision = useRef(0);
  useEffect(
    () => () => {
      copyRevision.current += 1;
    },
    [],
  );
  function change(name: string, value: string) {
    copyRevision.current += 1;
    setCopyingField(null);
    setNotices({});
    setValues((previous) => ({ ...previous, [name]: value }));
  }
  function toggle(field: Field) {
    const input = document.getElementById(id + '-' + field.name) as HTMLInputElement | null;
    const selection =
      input && ([input.selectionStart, input.selectionEnd, input.selectionDirection] as const);
    setVisible((previous) => ({ ...previous, [field.name]: !previous[field.name] }));
    requestAnimationFrame(() => {
      if (input?.isConnected && selection && selection[0] !== null && selection[1] !== null)
        input.setSelectionRange(selection[0], selection[1], selection[2] ?? undefined);
    });
  }
  function generate(field: Field) {
    try {
      const value = generatePassword();
      const confirmation = fields.find(
        (item) => item.name === field.confirmationField && item.passwordPurpose === 'confirmation',
      );
      copyRevision.current += 1;
      setCopyingField(null);
      setValues((previous) => ({
        ...previous,
        [field.name]: value,
        ...(confirmation ? { [confirmation.name]: value } : {}),
      }));
      setVisible((previous) => ({
        ...previous,
        [field.name]: false,
        ...(confirmation ? { [confirmation.name]: false } : {}),
      }));
      setErrors((previous) =>
        Object.fromEntries(
          Object.entries(previous).filter(
            ([name]) => name !== field.name && name !== confirmation?.name,
          ),
        ),
      );
      setNotices({ [field.name]: 'Contraseña generada. Puedes copiarla y guardarla.' });
    } catch {
      setNotices({ [field.name]: 'No pudimos generar una contraseña. Inténtalo de nuevo.' });
    }
  }
  async function copy(field: Field) {
    const revision = ++copyRevision.current;
    setCopyingField(field.name);
    setNotices({});
    try {
      await navigator.clipboard.writeText(values[field.name] ?? '');
      if (revision === copyRevision.current) setNotices({ [field.name]: 'Contraseña copiada.' });
    } catch {
      if (revision === copyRevision.current)
        setNotices({
          [field.name]: 'No pudimos copiarla. Puedes mostrarla y copiarla desde el campo.',
        });
    } finally {
      if (revision === copyRevision.current) setCopyingField(null);
    }
  }
  async function send(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    copyRevision.current += 1;
    setCopyingField(null);
    setNotices({});
    setVisible({});
    const next: Record<string, string> = {};
    for (const field of fields) {
      const value = values[field.name] ?? '';
      if (!value.trim()) next[field.name] = 'Completa este campo.';
      else if (
        field.type === 'email' &&
        (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value.trim()) || value.trim().length > 254)
      )
        next[field.name] = 'Escribe un correo electrónico válido.';
      else if (
        field.name === 'displayName' &&
        (value.trim().length < 2 ||
          value.trim().length > 100 ||
          Array.from(value).some(
            (character) => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127,
          ))
      )
        next[field.name] = 'Usa entre 2 y 100 caracteres.';
      else if (
        (field.name === 'newPassword' ||
          (field.name === 'password' && fields.some((item) => item.name === 'confirmPassword'))) &&
        (value.length < 15 || value.length > 128)
      )
        next[field.name] = 'Usa entre 15 y 128 caracteres.';
      else if (
        field.name === 'confirmPassword' &&
        value !== (values['newPassword'] ?? values['password'])
      )
        next[field.name] = 'Las contraseñas no coinciden.';
    }
    setErrors(next);
    setError('');
    if (Object.keys(next).length) {
      document.getElementById(id + '-' + Object.keys(next)[0])?.focus();
      return;
    }
    setBusy(true);
    try {
      await onSubmit(
        Object.fromEntries(
          Object.entries(values).map(([name, value]) => [
            name,
            /password/i.test(name) ? value : value.trim(),
          ]),
        ),
      );
    } catch (failure) {
      setError(
        failure instanceof ApiError
          ? failure.message
          : 'No pudimos completar la acción. Inténtalo de nuevo.',
      );
      if (failure instanceof ApiError)
        setErrors(
          Object.fromEntries(
            Object.keys(failure.fields).map((field) => [
              field.charAt(0).toLowerCase() + field.slice(1),
              'Revisa el valor de este campo.',
            ]),
          ),
        );
    } finally {
      setBusy(false);
    }
  }
  return (
    <form
      noValidate
      onSubmit={(event) => void send(event)}
      className="account-form"
      aria-busy={busy}
    >
      {error && (
        <div className="form-alert" role="alert">
          {error}
        </div>
      )}
      {fields.map((field) => (
        <div className="field" key={field.name}>
          <label htmlFor={id + '-' + field.name}>{field.label}</label>
          <div className={field.type === 'password' ? 'password-control' : undefined}>
            <input
              id={id + '-' + field.name}
              name={field.name}
              type={
                field.type === 'password' && visible[field.name] ? 'text' : (field.type ?? 'text')
              }
              autoComplete={field.autoComplete}
              value={values[field.name] ?? ''}
              onChange={(event) => change(field.name, event.target.value)}
              aria-invalid={Boolean(errors[field.name])}
              aria-describedby={
                [
                  field.help || errors[field.name] ? id + '-' + field.name + '-help' : '',
                  field.passwordPurpose === 'new' ? id + '-' + field.name + '-strength' : '',
                ]
                  .filter(Boolean)
                  .join(' ') || undefined
              }
              disabled={busy}
            />
            {field.type === 'password' && (
              <button
                type="button"
                className="password-toggle"
                aria-label={
                  (visible[field.name] ? 'Ocultar contraseña: ' : 'Mostrar contraseña: ') +
                  field.label
                }
                aria-controls={id + '-' + field.name}
                disabled={busy}
                onPointerDown={(event) => event.preventDefault()}
                onClick={() => toggle(field)}
              >
                <Icon name={visible[field.name] ? 'eye-off' : 'eye'} />
              </button>
            )}
          </div>
          {(field.help || errors[field.name]) && (
            <p
              id={id + '-' + field.name + '-help'}
              className={errors[field.name] ? 'field-error' : 'field-help'}
            >
              {errors[field.name] ?? field.help}
            </p>
          )}
          {field.type === 'password' && field.passwordPurpose === 'new' && (
            <>
              <PasswordStrengthMeter
                id={id + '-' + field.name + '-strength'}
                value={values[field.name] ?? ''}
                userInputs={[values['displayName'] ?? '', values['email'] ?? '']}
              />
              <div className="password-actions">
                <button
                  type="button"
                  className="password-action"
                  disabled={busy}
                  onClick={() => generate(field)}
                >
                  Generar contraseña
                </button>
                <button
                  type="button"
                  className="password-action"
                  disabled={busy || !values[field.name] || copyingField === field.name}
                  onClick={() => void copy(field)}
                >
                  {copyingField === field.name ? 'Copiando…' : 'Copiar contraseña'}
                </button>
              </div>
              {notices[field.name] && (
                <p className="field-help password-notice" role="status">
                  {notices[field.name]}
                </p>
              )}
            </>
          )}
        </div>
      ))}
      {children}
      <button
        className={submitVariant === 'secondary' ? 'button button-outline' : 'button'}
        type="submit"
        disabled={busy}
      >
        {busy ? 'Un momento…' : submit}
      </button>
    </form>
  );
}
