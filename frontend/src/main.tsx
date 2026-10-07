import './fonts.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { createAppRouter } from './router';
import './styles.css';
const root = document.getElementById('root');
if (!root) throw new Error('No se encontró el punto de entrada de la aplicación.');
const router = createAppRouter();
if (import.meta.hot) import.meta.hot.dispose(() => router.dispose());
createRoot(root).render(
  <StrictMode>
    <App router={router} />
  </StrictMode>,
);
