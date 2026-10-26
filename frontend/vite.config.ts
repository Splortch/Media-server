import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5050',
      '/health': 'http://127.0.0.1:5050',
      '/hubs': { target: 'http://127.0.0.1:5050', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    exclude: ['tests/e2e/**', 'node_modules/**'],
  },
});
