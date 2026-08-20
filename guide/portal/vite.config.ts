import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The portal calls the API same-origin ("/") and the dev server proxies every Sleipnir
// path to the Story.Api on 5010. That keeps CORS and the self-signed dev cert out of the
// browser's way — and lets the unified transport's `auto` mode probe WebSocket
// (/sleipnirws, ws proxied) and fall back to REST+SSE through the same proxy.
export default defineConfig({
  plugins: [svelte()],
  server: {
    port: 5173,
    proxy: {
      '/api/sleipnir': { target: 'https://localhost:5010', changeOrigin: true, secure: false },
      '/events': { target: 'https://localhost:5010', changeOrigin: true, secure: false },
      '/sleipnirws': { target: 'https://localhost:5010', changeOrigin: true, secure: false, ws: true },
      '/sleipnirhub': { target: 'https://localhost:5010', changeOrigin: true, secure: false, ws: true },
      '/Sleipnir': { target: 'https://localhost:5010', changeOrigin: true, secure: false }
    }
  }
});