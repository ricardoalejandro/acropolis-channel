import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/inter/latin-400.css';
import '@fontsource/inter/latin-500.css';
import '@fontsource/inter/latin-600.css';
import '@fontsource/inter/latin-700.css';
import '@fontsource/source-serif-4/latin-400.css';
import '@fontsource/source-serif-4/latin-400-italic.css';
import Preview from './Preview';
import '../styles.css';
import './preview.css';
const root = document.getElementById('root');
if (!root) throw new Error('No se encontró el punto de entrada.');
createRoot(root).render(
  <StrictMode>
    <Preview />
  </StrictMode>,
);
