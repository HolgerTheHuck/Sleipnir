import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api/sleipnir': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false
      },
      '/sleipnirws': {
        target: 'wss://localhost:5001',
        ws: true,
        changeOrigin: true,
        secure: false
      },
      '/sleipnirhub': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      '/Sleipnir': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false
      }
    }
  }
});
