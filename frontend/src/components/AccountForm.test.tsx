import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/identity';
import { generatePassword } from '../validation/passwordGeneration';
import { estimatePasswordStrength } from '../validation/passwordStrength';
import { AccountForm, type Field } from './AccountForm';
vi.mock('../validation/passwordGeneration', () => ({ generatePassword: vi.fn() }));
vi.mock('../validation/passwordStrength', () => ({ estimatePasswordStrength: vi.fn() }));
const registration: Field[] = [
  { name: 'displayName', label: 'Nombre visible' },
  { name: 'email', label: 'Correo electrónico', type: 'email' },
  {
    name: 'password',
    label: 'Contraseña',
    type: 'password',
    passwordPurpose: 'new',
    confirmationField: 'confirmPassword',
    autoComplete: 'new-password',
  },
  {
    name: 'confirmPassword',
    label: 'Confirmar contraseña',
    type: 'password',
    passwordPurpose: 'confirmation',
    autoComplete: 'new-password',
  },
];
function fill(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}
beforeEach(() => {
  vi.mocked(generatePassword).mockReset().mockReturnValue('Q7!kP2@tY9#vN3$rL5%w');
  vi.mocked(estimatePasswordStrength)
    .mockReset()
    .mockResolvedValue({ score: 2, warning: '', suggestions: [] });
});
describe('Password entry assistance', () => {
  function mountRegistration(onSubmit = vi.fn().mockResolvedValue(undefined)) {
    render(<AccountForm fields={registration} submit="Crear cuenta" onSubmit={onSubmit} />);
    return onSubmit;
  }
  function fillIdentity() {
    fill('Nombre visible', 'Persona Nueva');
    fill('Correo electrónico', 'persona@example.test');
  }
  it('starts hidden and allows independent keyboard toggles without submitting or losing text', async () => {
    const save = mountRegistration();
    const password = screen.getByLabelText('Contraseña') as HTMLInputElement;
    const confirmation = screen.getByLabelText('Confirmar contraseña');
    const value = '  Árboles 秘密 con espacios  ';
    fill('Contraseña', value);
    fill('Confirmar contraseña', value);
    expect(password).toHaveAttribute('type', 'password');
    expect(confirmation).toHaveAttribute('type', 'password');
    expect(password).toHaveAttribute('autocomplete', 'new-password');
    password.focus();
    password.setSelectionRange(2, 9);
    await userEvent.tab();
    const show = screen.getByRole('button', { name: 'Mostrar contraseña: Contraseña' });
    expect(show).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    expect(password).toHaveAttribute('type', 'text');
    expect(password).toHaveValue(value);
    expect(password.selectionStart).toBe(2);
    expect(password.selectionEnd).toBe(9);
    expect(confirmation).toHaveAttribute('type', 'password');
    await userEvent.click(screen.getByRole('button', { name: 'Ocultar contraseña: Contraseña' }));
    expect(password).toHaveAttribute('type', 'password');
    expect(password).toHaveValue(value);
    await userEvent.click(
      screen.getByRole('button', { name: 'Mostrar contraseña: Confirmar contraseña' }),
    );
    expect(confirmation).toHaveAttribute('type', 'text');
    expect(save).not.toHaveBeenCalled();
  });
  it('offers only visibility for current and unspecified password fields', async () => {
    const save = vi.fn();
    render(
      <AccountForm
        fields={[
          {
            name: 'password',
            label: 'Contraseña actual',
            type: 'password',
            passwordPurpose: 'current',
            autoComplete: 'current-password',
          },
          { name: 'backupPassword', label: 'Otra contraseña', type: 'password' },
        ]}
        submit="Ingresar"
        onSubmit={save}
      />,
    );
    expect(screen.queryByRole('button', { name: 'Generar contraseña' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Copiar contraseña' })).not.toBeInTheDocument();
    expect(screen.queryByText('Fortaleza estimada')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Contraseña actual')).toHaveAttribute(
      'autocomplete',
      'current-password',
    );
    await userEvent.click(
      screen.getByRole('button', { name: 'Mostrar contraseña: Otra contraseña' }),
    );
    expect(screen.getByLabelText('Otra contraseña')).toHaveAttribute('type', 'text');
    expect(estimatePasswordStrength).not.toHaveBeenCalled();
    expect(save).not.toHaveBeenCalled();
  });
  it('generates once, replaces both fields together and hides any visible password', async () => {
    const save = mountRegistration();
    fill('Contraseña', 'Una frase anterior muy larga');
    fill('Confirmar contraseña', 'Una confirmación distinta');
    await userEvent.click(screen.getByRole('button', { name: 'Mostrar contraseña: Contraseña' }));
    await userEvent.click(
      screen.getByRole('button', { name: 'Mostrar contraseña: Confirmar contraseña' }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Generar contraseña' }));
    expect(generatePassword).toHaveBeenCalledOnce();
    for (const label of ['Contraseña', 'Confirmar contraseña']) {
      expect(screen.getByLabelText(label)).toHaveValue('Q7!kP2@tY9#vN3$rL5%w');
      expect(screen.getByLabelText(label)).toHaveAttribute('type', 'password');
    }
    expect(save).not.toHaveBeenCalled();
    fillIdentity();
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    expect(save).toHaveBeenCalledWith({
      displayName: 'Persona Nueva',
      email: 'persona@example.test',
      password: 'Q7!kP2@tY9#vN3$rL5%w',
      confirmPassword: 'Q7!kP2@tY9#vN3$rL5%w',
    });
  });
  it('preserves both previous values when secure generation fails', async () => {
    vi.mocked(generatePassword).mockImplementation(() => {
      throw new Error('sensitive random failure');
    });
    const save = mountRegistration();
    fill('Contraseña', '  Una frase anterior larga  ');
    fill('Confirmar contraseña', '  Confirmación anterior distinta  ');
    await userEvent.click(screen.getByRole('button', { name: 'Generar contraseña' }));
    expect(screen.getByLabelText('Contraseña')).toHaveValue('  Una frase anterior larga  ');
    expect(screen.getByLabelText('Confirmar contraseña')).toHaveValue(
      '  Confirmación anterior distinta  ',
    );
    expect(screen.getByText(/no pudimos generar/i)).toBeInTheDocument();
    expect(screen.queryByText('sensitive random failure')).not.toBeInTheDocument();
    expect(save).not.toHaveBeenCalled();
  });
  it('copies only after an explicit action and announces success without echoing the password', async () => {
    const save = mountRegistration();
    const user = userEvent.setup();
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined);
    const copy = screen.getByRole('button', { name: 'Copiar contraseña' });
    expect(copy).toBeDisabled();
    const value = '  Árboles 秘密 con espacios  ';
    fill('Contraseña', value);
    expect(writeText).not.toHaveBeenCalled();
    await user.click(copy);
    expect(writeText).toHaveBeenCalledOnce();
    expect(writeText).toHaveBeenCalledWith(value);
    expect(screen.getByText(/contraseña copiada/i)).toBeInTheDocument();
    expect(screen.queryByText(value)).not.toBeInTheDocument();
    expect(save).not.toHaveBeenCalled();
    fill('Contraseña', value + ' nueva');
    expect(screen.queryByText(/contraseña copiada/i)).not.toBeInTheDocument();
  });
  it('allows manual copying after clipboard rejection without losing the password', async () => {
    mountRegistration();
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(
      new Error('sensitive clipboard failure'),
    );
    const value = 'Una frase larga para copiar';
    fill('Contraseña', value);
    await user.click(screen.getByRole('button', { name: 'Copiar contraseña' }));
    expect(screen.getByText(/no pudimos copiar/i)).toBeInTheDocument();
    expect(screen.queryByText('sensitive clipboard failure')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Mostrar contraseña: Contraseña' }));
    expect(screen.getByLabelText('Contraseña')).toHaveAttribute('type', 'text');
    expect(screen.getByLabelText('Contraseña')).toHaveValue(value);
  });
  it('ignores a clipboard completion for a password that has since been edited', async () => {
    mountRegistration();
    const user = userEvent.setup();
    let complete: (() => void) | undefined;
    vi.spyOn(navigator.clipboard, 'writeText').mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          complete = resolve;
        }),
    );
    fill('Contraseña', 'Una frase anterior para copiar');
    await user.click(screen.getByRole('button', { name: 'Copiar contraseña' }));
    fill('Contraseña', 'Una frase nueva para conservar');
    await act(async () => complete?.());
    expect(screen.queryByText(/contraseña copiada/i)).not.toBeInTheDocument();
    expect(screen.getByLabelText('Contraseña')).toHaveValue('Una frase nueva para conservar');
  });
  it('disables all password controls while submitting and permits them again afterwards', async () => {
    let finish: (() => void) | undefined;
    const save = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    mountRegistration(save);
    fillIdentity();
    fill('Contraseña', 'Una frase larga para entrar');
    fill('Confirmar contraseña', 'Una frase larga para entrar');
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    for (const button of screen.getAllByRole('button')) expect(button).toBeDisabled();
    expect(screen.getByLabelText('Contraseña')).toBeDisabled();
    expect(screen.getByLabelText('Confirmar contraseña')).toBeDisabled();
    await act(async () => finish?.());
    for (const button of screen.getAllByRole('button')) expect(button).toBeEnabled();
  });
  it('debounces estimation for 200 ms and displays a text level as well as the meter', async () => {
    vi.useFakeTimers();
    mountRegistration();
    fill('Contraseña', 'Un primer texto de contraseña');
    await act(async () => vi.advanceTimersByTime(199));
    expect(estimatePasswordStrength).not.toHaveBeenCalled();
    fill('Contraseña', 'Otro texto distinto y más largo');
    await act(async () => vi.advanceTimersByTime(199));
    expect(estimatePasswordStrength).not.toHaveBeenCalled();
    await act(async () => vi.advanceTimersByTime(1));
    expect(estimatePasswordStrength).toHaveBeenCalledOnce();
    expect(estimatePasswordStrength).toHaveBeenCalledWith(
      'Otro texto distinto y más largo',
      expect.any(Array),
    );
    expect(screen.getByText('Moderada')).toBeInTheDocument();
    expect(screen.getByRole('meter', { name: 'Fortaleza estimada' })).toHaveAttribute(
      'aria-valuetext',
      'Moderada',
    );
  });
  it('does not replace a newer strength result with a stale asynchronous estimate', async () => {
    vi.useFakeTimers();
    let resolveOld:
      ((value: { score: 0; warning: string; suggestions: string[] }) => void) | undefined;
    vi.mocked(estimatePasswordStrength)
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveOld = resolve;
          }),
      )
      .mockResolvedValueOnce({ score: 4, warning: '', suggestions: [] });
    mountRegistration();
    fill('Contraseña', 'aaaaaaaaaaaaaaa');
    await act(async () => vi.advanceTimersByTime(200));
    fill('Contraseña', 'Una frase nueva larga y distinta');
    await act(async () => vi.advanceTimersByTime(200));
    expect(screen.getByText('Muy fuerte')).toBeInTheDocument();
    await act(async () => resolveOld?.({ score: 0, warning: '', suggestions: [] }));
    expect(screen.getByText('Muy fuerte')).toBeInTheDocument();
    expect(screen.queryByText('Muy débil')).not.toBeInTheDocument();
  });
  it.each([
    [0, 'Muy débil'],
    [1, 'Débil'],
    [2, 'Moderada'],
    [3, 'Fuerte'],
    [4, 'Muy fuerte'],
  ] as const)('announces level %i as %s without relying on color', async (score, label) => {
    vi.mocked(estimatePasswordStrength).mockResolvedValue({ score, warning: '', suggestions: [] });
    mountRegistration();
    fill('Contraseña', 'Una frase larga para evaluar');
    expect(await screen.findByText(label)).toBeInTheDocument();
    const meter = screen.getByRole('meter', { name: 'Fortaleza estimada' });
    expect(meter).toHaveAttribute('aria-valuemin', '0');
    expect(meter).toHaveAttribute('aria-valuemax', '4');
    expect(meter).toHaveAttribute('aria-valuenow', String(score));
    expect(meter).toHaveAttribute('aria-valuetext', label);
  });
  it.each([
    [14, false],
    [15, true],
    [128, true],
    [129, false],
  ] as const)(
    'preserves the length rule for %i characters regardless of estimated score',
    async (length, accepted) => {
      vi.mocked(estimatePasswordStrength).mockResolvedValue({
        score: 4,
        warning: '',
        suggestions: [],
      });
      const save = mountRegistration();
      fillIdentity();
      fill('Contraseña', 'a'.repeat(length));
      fill('Confirmar contraseña', 'a'.repeat(length));
      await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
      if (accepted) expect(save).toHaveBeenCalledOnce();
      else {
        expect(save).not.toHaveBeenCalled();
        expect(screen.getByLabelText('Contraseña')).toHaveAttribute('aria-invalid', 'true');
        expect(screen.getByLabelText('Contraseña')).toHaveFocus();
      }
    },
  );
  it('re-estimates with the current name and email without changing or submitting the password', async () => {
    vi.useFakeTimers();
    const save = mountRegistration();
    fill('Contraseña', 'Una frase larga para evaluar');
    await act(async () => vi.advanceTimersByTime(200));
    fillIdentity();
    await act(async () => vi.advanceTimersByTime(200));
    expect(estimatePasswordStrength).toHaveBeenLastCalledWith('Una frase larga para evaluar', [
      'Persona Nueva',
      'persona@example.test',
    ]);
    expect(screen.getByLabelText('Contraseña')).toHaveValue('Una frase larga para evaluar');
    expect(save).not.toHaveBeenCalled();
  });
  it('skips oversized input and permits a valid low-strength password', async () => {
    const save = mountRegistration();
    fill('Contraseña', 'a'.repeat(129));
    await new Promise((resolve) => setTimeout(resolve, 250));
    expect(estimatePasswordStrength).not.toHaveBeenCalled();
    expect(screen.getByText(/128 caracteres/)).toBeInTheDocument();
    vi.mocked(estimatePasswordStrength).mockResolvedValue({
      score: 0,
      warning: '',
      suggestions: [],
    });
    fillIdentity();
    fill('Contraseña', 'a'.repeat(15));
    fill('Confirmar contraseña', 'a'.repeat(15));
    expect(await screen.findByText('Muy débil')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    expect(save).toHaveBeenCalledOnce();
  });
  it('keeps a valid submission available if the strength estimator fails', async () => {
    vi.mocked(estimatePasswordStrength).mockRejectedValue(new Error('sensitive estimator failure'));
    const save = mountRegistration();
    fillIdentity();
    fill('Contraseña', 'Una frase válida para guardar');
    fill('Confirmar contraseña', 'Una frase válida para guardar');
    expect(
      await screen.findByText(/medidor.*no disponible|no pudimos estimar/i),
    ).toBeInTheDocument();
    expect(screen.queryByText('sensitive estimator failure')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Crear cuenta' }));
    expect(save).toHaveBeenCalledOnce();
  });
});
describe('Account form validation and accessible async state', () => {
  it('blocks missing fields, connects feedback to inputs and focuses the first error', async () => {
    const save = vi.fn();
    render(<AccountForm fields={registration} submit="Guardar" onSubmit={save} />);
    const saveButton = screen.getByRole('button', { name: 'Guardar' });
    expect(saveButton).toHaveClass('button');
    expect(saveButton).not.toHaveClass('button-outline');
    await userEvent.click(saveButton);
    expect(save).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Nombre visible')).toHaveFocus();
    expect(screen.getByLabelText('Correo electrónico')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getAllByText('Completa este campo.')).toHaveLength(4);
  });
  it('rejects invalid email, short/long names and short/long passwords before HTTP mutation', async () => {
    const save = vi.fn();
    render(<AccountForm fields={registration} submit="Guardar" onSubmit={save} />);
    fill('Nombre visible', 'x');
    fill('Correo electrónico', 'wrong');
    fill('Contraseña', 'short');
    fill('Confirmar contraseña', 'different');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    expect(screen.getByText('Usa entre 2 y 100 caracteres.')).toBeInTheDocument();
    expect(screen.getByText('Escribe un correo electrónico válido.')).toBeInTheDocument();
    expect(screen.getByText('Usa entre 15 y 128 caracteres.')).toBeInTheDocument();
    expect(screen.getByText('Las contraseñas no coinciden.')).toBeInTheDocument();
    fill('Nombre visible', 'a'.repeat(101));
    fill('Correo electrónico', 'a'.repeat(250) + '@b.test');
    fill('Contraseña', 'a'.repeat(129));
    fill('Confirmar contraseña', 'a'.repeat(129));
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    expect(save).not.toHaveBeenCalled();
  });
  it('trims identity fields but preserves password whitespace and avoids duplicate pending submissions', async () => {
    let resolve: (() => void) | undefined;
    const save = vi.fn(
      () =>
        new Promise<void>((done) => {
          resolve = done;
        }),
    );
    const { container } = render(
      <AccountForm
        fields={[
          { name: 'displayName', label: 'Nombre', initial: 'Persona' },
          { name: 'password', label: 'Clave', type: 'password', help: 'Mantén tu clave privada.' },
        ]}
        submit="Guardar"
        onSubmit={save}
      />,
    );
    fill('Nombre', '  Persona Nueva  ');
    fill('Clave', '  contraseña con espacios  ');
    fireEvent.submit(container.querySelector('form') as HTMLFormElement);
    fireEvent.submit(container.querySelector('form') as HTMLFormElement);
    expect(save).toHaveBeenCalledOnce();
    expect(save).toHaveBeenCalledWith({
      displayName: 'Persona Nueva',
      password: '  contraseña con espacios  ',
    });
    expect(screen.getByRole('button', { name: 'Un momento…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Un momento…' })).toHaveTextContent('Un momento');
    resolve?.();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled());
  });
  it('supports a secondary action without fields and keeps its async state accessible', async () => {
    let resolve: (() => void) | undefined;
    const save = vi.fn(
      () =>
        new Promise<void>((done) => {
          resolve = done;
        }),
    );
    render(
      <AccountForm
        fields={[]}
        submit="Reenviar enlace"
        submitVariant="secondary"
        onSubmit={save}
      />,
    );
    const button = screen.getByRole('button', { name: 'Reenviar enlace' });
    expect(button).toHaveClass('button', 'button-outline');
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(save).not.toHaveBeenCalled();
    await userEvent.click(button);
    expect(save).toHaveBeenCalledWith({});
    expect(button).toBeDisabled();
    expect(button.closest('form')).toHaveAttribute('aria-busy', 'true');
    resolve?.();
    await waitFor(() => expect(button).toBeEnabled());
    expect(button).toHaveClass('button-outline');
    expect(button.closest('form')).toHaveAttribute('aria-busy', 'false');
  });
  it('shows safe field errors from the server and recovers after a retry', async () => {
    const save = vi
      .fn()
      .mockRejectedValueOnce(
        new ApiError(400, 'validation_error', { DisplayName: ['unsafe detail'] }),
      )
      .mockResolvedValueOnce(undefined);
    render(
      <AccountForm
        fields={[{ name: 'displayName', label: 'Nombre', initial: 'Persona' }]}
        submit="Guardar"
        onSubmit={save}
      />,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Revisa los campos');
    expect(screen.getByText('Revisa el valor de este campo.')).toBeInTheDocument();
    expect(screen.queryByText('unsafe detail')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });
  it('sanitizes unexpected failures and validates new-password confirmation', async () => {
    render(
      <AccountForm
        fields={[
          {
            name: 'newPassword',
            label: 'Nueva',
            type: 'password',
            passwordPurpose: 'new',
            confirmationField: 'confirmPassword',
          },
          {
            name: 'confirmPassword',
            label: 'Repetir',
            type: 'password',
            passwordPurpose: 'confirmation',
          },
        ]}
        submit="Guardar"
        onSubmit={async () => {
          throw new Error('sensitive failure');
        }}
      />,
    );
    fill('Nueva', 'Una frase muy larga y nueva');
    fill('Repetir', 'Otra frase muy larga distinta');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    expect(screen.getByText('Las contraseñas no coinciden.')).toBeInTheDocument();
    fill('Repetir', 'Una frase muy larga y nueva');
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos completar la acción');
    expect(screen.queryByText('sensitive failure')).not.toBeInTheDocument();
  });
});
