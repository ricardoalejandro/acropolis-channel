import '../fonts.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
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
