import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Modus-abhängige Pfade:
// - default (eingebettet): base = /developer-static/developer/ (Static-Web-Asset-
//   Pfad der NuGet-Pakets), outDir = wwwroot/developer (wird vom csproj gepackt).
// - standalone: base = ./ (relative Assets) → läuft von beliebigem Host/Pfad
//   (GitHub Pages, lokaler serve), outDir = dist-standalone (npm-Paket/Deploy).
export default defineConfig(({ mode }) => ({
  plugins: [svelte()],
  base: mode === 'standalone' ? './' : '/developer-static/developer/',
  build: {
    outDir: mode === 'standalone' ? 'dist-standalone' : 'wwwroot/developer',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'https://localhost:5001',
        secure: false,
      },
    },
  },
}));