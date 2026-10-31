import React from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { initializeTheme } from '../../../frontend/src/theme/themes';
import '../../../frontend/src/styles/app.css';
import './desktop.css';
initializeTheme();
createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
