import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    port: 5173,
    proxy: {
      '/api/sleipnir': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false
      },
      '/sleipnirws': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      '/sleipnirhub': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      '/Sleipnir': {
        target: 'https://localhost:5002',
        changeOrigin: true,
        secure: false
      }
    }
  }
});
