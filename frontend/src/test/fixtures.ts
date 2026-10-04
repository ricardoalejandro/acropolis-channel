import type { User } from '../api/identity';
export const user: User = {
  id: 'test-person',
  displayName: 'Persona de Prueba',
  email: 'persona@example.test',
  emailConfirmed: true,
  status: 'active',
  levels: ['Externo'],
  permissions: [],
  version: 'version-one',
};
export const admin: User = {
  ...user,
  id: 'test-admin',
  displayName: 'Administradora de Prueba',
  permissions: ['Users.Manage'],
};
export function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
