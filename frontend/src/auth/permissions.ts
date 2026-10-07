export const administrativeLinks = [
  { permission: 'Users.Manage', path: '/admin/users', label: 'Usuarios', icon: 'user' },
  { permission: 'Content.Manage', path: '/admin/content', label: 'Contenidos', icon: 'book' },
  { permission: 'Content.Manage', path: '/admin/topics', label: 'Temas', icon: 'book' },
  {
    permission: 'Subscriptions.Manage',
    path: '/admin/subscriptions',
    label: 'Suscripciones',
    icon: 'shield',
  },
];
export const permissionLabels: Record<string, string> = {
  'Users.Manage': 'Gestionar usuarios',
  'Content.Manage': 'Gestionar contenidos',
  'Subscriptions.Manage': 'Gestionar suscripciones',
};
