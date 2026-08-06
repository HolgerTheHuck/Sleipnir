import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    port: 5173,
    proxy: {
      '/api/trame': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false
      },
      '/tramews': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      '/tramehub': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      '/Trame': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false
      }
    }
  }
});
