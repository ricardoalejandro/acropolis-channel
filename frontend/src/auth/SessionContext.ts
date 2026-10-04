import { createContext } from 'react';
import type { User } from '../api/identity';
export type Session = {
  user: User | null;
  loading: boolean;
  notice: string;
  setUser: (user: User | null, message?: string) => void;
};
export const SessionContext = createContext<Session | null>(null);
