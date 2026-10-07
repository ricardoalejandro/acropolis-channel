import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError } from '../api/identity';
export type Field = {
  name: string;
  label: string;
  type?: string;
  autoComplete?: string;
  help?: string;
  initial?: string;
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
  async function send(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
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
          <input
            id={id + '-' + field.name}
            name={field.name}
            type={field.type ?? 'text'}
            autoComplete={field.autoComplete}
            value={values[field.name] ?? ''}
            onChange={(event) => setValues({ ...values, [field.name]: event.target.value })}
            aria-invalid={Boolean(errors[field.name])}
            aria-describedby={
              field.help || errors[field.name] ? id + '-' + field.name + '-help' : undefined
            }
            disabled={busy}
          />
          {(field.help || errors[field.name]) && (
            <p
              id={id + '-' + field.name + '-help'}
              className={errors[field.name] ? 'field-error' : 'field-help'}
            >
              {errors[field.name] ?? field.help}
            </p>
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
