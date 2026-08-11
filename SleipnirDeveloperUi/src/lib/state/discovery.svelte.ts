import { fetchDiscovery as apiFetchDiscovery } from '../api/client';
import type { DiscoveryInfo, ControllerMeta } from 'sleipnir-client';

class DiscoveryState {
  data = $state<DiscoveryInfo | null>(null);
  loading = $state(false);
  error = $state<string | null>(null);
  searchQuery = $state('');

  get filteredControllers(): ControllerMeta[] {
    if (!this.data) return [];
    const q = this.searchQuery.toLowerCase().trim();
    if (!q) return this.data.controllers;

    return this.data.controllers
      .map((c) => ({
        ...c,
        methods: c.methods.filter(
          (m) =>
            c.name.toLowerCase().includes(q) ||
            m.methodName.toLowerCase().includes(q)
        ),
      }))
      .filter((c) => c.methods.length > 0 || c.name.toLowerCase().includes(q));
  }

  async fetchDiscovery() {
    this.loading = true;
    this.error = null;
    try {
      this.data = await apiFetchDiscovery();
    } catch (err) {
      // Fehler NICHT nur in den Error-State schreiben — sonst ist er in der
      // Browser-Console unsichtbar und ein stillschlagender Fetch (CORS/CSP/
      // gemischte Content-/Netzwerkfehler) lässt die DevUI ohne Diagnose dumm
      // stehen. Stack ins Console, message in den Error-Text (Explorer-Pane).
      console.error('[Sleipnir DevUI] Discovery-Fetch fehlgeschlagen:', err);
      this.error = err instanceof Error ? err.message : 'Discovery failed';
    } finally {
      this.loading = false;
    }
  }
}

export const discoveryState = new DiscoveryState();
