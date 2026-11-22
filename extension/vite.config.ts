import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';

export default defineConfig({
  plugins: [preact()],
  // Relative paths: Twitch serves the extension from its CDN under a sub path.
  base: './',
  server: { proxy: { '/api': 'http://127.0.0.1:5090' } },
});
