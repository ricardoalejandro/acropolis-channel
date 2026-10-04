import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/identity';
import { AccountForm, type Field } from './AccountForm';
const registration: Field[] = [
  { name: 'displayName', label: 'Nombre visible' },
  { name: 'email', label: 'Correo electrónico', type: 'email' },
  { name: 'password', label: 'Contraseña', type: 'password' },
  { name: 'confirmPassword', label: 'Confirmar contraseña', type: 'password' },
];
function fill(label: string, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}
describe('Account form validation and accessible async state', () => {
  it('blocks missing fields, connects feedback to inputs and focuses the first error', async () => {
    const save = vi.fn();
    render(<AccountForm fields={registration} submit="Guardar" onSubmit={save} />);
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }));
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
    expect(screen.getByRole('button')).toBeDisabled();
    expect(screen.getByRole('button')).toHaveTextContent('Un momento');
    resolve?.();
    await waitFor(() => expect(screen.getByRole('button')).toBeEnabled());
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
    await userEvent.click(screen.getByRole('button'));
    expect(await screen.findByRole('alert')).toHaveTextContent('Revisa los campos');
    expect(screen.getByText('Revisa el valor de este campo.')).toBeInTheDocument();
    expect(screen.queryByText('unsafe detail')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button'));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });
  it('sanitizes unexpected failures and validates new-password confirmation', async () => {
    render(
      <AccountForm
        fields={[
          { name: 'newPassword', label: 'Nueva', type: 'password' },
          { name: 'confirmPassword', label: 'Repetir', type: 'password' },
        ]}
        submit="Guardar"
        onSubmit={async () => {
          throw new Error('sensitive failure');
        }}
      />,
    );
    fill('Nueva', 'Una frase muy larga y nueva');
    fill('Repetir', 'Otra frase muy larga distinta');
    await userEvent.click(screen.getByRole('button'));
    expect(screen.getByText('Las contraseñas no coinciden.')).toBeInTheDocument();
    fill('Repetir', 'Una frase muy larga y nueva');
    await userEvent.click(screen.getByRole('button'));
    expect(await screen.findByRole('alert')).toHaveTextContent('No pudimos completar la acción');
    expect(screen.queryByText('sensitive failure')).not.toBeInTheDocument();
  });
});
