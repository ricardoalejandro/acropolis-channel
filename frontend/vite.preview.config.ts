import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist-preview', sourcemap: false, rollupOptions: { input: 'preview.html' } },
  server: {
    port: 4174,
    strictPort: true,
    allowedHosts: ['preview', 'acropolis_test_identity_design_preview'],
  },
  preview: { port: 4174 },
});
