import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api/trame': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false
      },
      '/tramews': {
        target: 'wss://localhost:5001',
        ws: true,
        changeOrigin: true,
        secure: false
      },
      '/tramehub': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      '/Trame': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false
      }
    }
  }
});
