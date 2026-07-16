<script lang="ts">
  import { discoveryState } from '../state/discovery.svelte.ts';
  import { tabState } from '../state/tabs.svelte.ts';
  import { themeState } from '../state/theme.svelte.ts';
  import { historyState } from '../state/history.svelte.ts';
  import { authState } from '../state/auth.svelte.ts';
  import { endpointState } from '../state/endpoint.svelte.ts';
  import { exportWorkspaceFile, importWorkspaceFromText } from '../state/workspace';

  let authOpen = $state(false);
  let tokenInput = $state('');
  let settingsOpen = $state(false);
  let baseUrlInput = $state(endpointState.baseUrl);
  let apiPathInput = $state(endpointState.apiPath);
  let importError = $state('');
  let fileInput = $state<HTMLInputElement | null>(null);

  function toggleAuth() {
    // Beim Öffnen das Input mit dem aktuell gesetzten Token vorbelegen.
    tokenInput = authState.bearer;
    authOpen = !authOpen;
  }

  function applyToken() {
    authState.set(tokenInput);
    authOpen = false;
    // Discovery neu laden: der vorige Fetch lief u. U. ohne Token (401) — mit
    // Token stehen geschützte Controller/Methoden jetzt zur Verfügung.
    discoveryState.fetchDiscovery();
  }

  function clearToken() {
    authState.clear();
    tokenInput = '';
    authOpen = false;
    discoveryState.fetchDiscovery();
  }

  function toggleSettings() {
    // Beim Öffnen die Inputs mit der aktuellen Connection vorbelegen (falls der
    // User zwischen Öffnen/Apply ändert).
    baseUrlInput = endpointState.baseUrl;
    apiPathInput = endpointState.apiPath;
    importError = '';
    settingsOpen = !settingsOpen;
  }

  function applyConnection() {
    endpointState.set(baseUrlInput, apiPathInput);
    settingsOpen = false;
    // Neuer Endpoint → Discovery neu laden (anderer Server → andere Metadaten).
    discoveryState.fetchDiscovery();
  }

  function resetConnection() {
    baseUrlInput = '/';
    apiPathInput = 'api/trame';
    endpointState.set('/', 'api/trame');
    settingsOpen = false;
    discoveryState.fetchDiscovery();
  }

  function handleExport() {
    exportWorkspaceFile();
    settingsOpen = false;
  }

  function handleImportClick() {
    importError = '';
    fileInput?.click();
  }

  function handleImportFile(e: Event) {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    file
      .text()
      .then((text) => {
        try {
          importWorkspaceFromText(text);
          settingsOpen = false;
          // Endpoint wurde aus dem Workspace übernommen → Discovery neu laden.
          discoveryState.fetchDiscovery();
        } catch (err) {
          importError = err instanceof Error ? err.message : String(err);
        }
      })
      .catch(() => {
        importError = 'Datei konnte nicht gelesen werden.';
      });
    // Value zurücksetzen, damit dieselbe Datei erneut gewählt werden kann.
    input.value = '';
  }
</script>

<header class="topbar">
  <div class="brand">
    <!-- Trame = French "weft": the cross-threads that hold a fabric together.
         Brand mark: two woven threads (green + blue) instead of three colored dots. -->
    <svg class="brand-mark" width="16" height="16" viewBox="0 0 16 16" fill="none" stroke-width="1.9" stroke-linecap="round" aria-hidden="true" title="Trame — woven threads">
      <path d="M2 5 C 5 5, 5 11, 8 11 S 11 5, 14 5" stroke="var(--success)"></path>
      <path d="M2 11 C 5 11, 5 5, 8 5 S 11 11, 14 11" stroke="var(--accent-secondary)"></path>
    </svg>
    <span class="title">Trame Developer</span>
  </div>

  <div class="actions">
    <span class="pill" title="Aktiver API-Pfad">{endpointState.baseUrl === '/' ? endpointState.apiPath : `${endpointState.baseUrl}${endpointState.apiPath}`}</span>

    <div class="dropdown-wrap">
      <button
        class="ghost small"
        class:active={endpointState.isCustom}
        onclick={toggleSettings}
        title="Verbindung & Workspace (Endpoint, Export, Import)"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <circle cx="12" cy="12" r="3"></circle>
          <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09A1.65 1.65 0 0 0 15 4.6a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9c.2.56.69.97 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"></path>
        </svg>
        Settings
        {#if endpointState.isCustom}<span class="auth-dot" title="Custom Endpoint aktiv"></span>{/if}
      </button>

      {#if settingsOpen}
        <!-- Backdrop fängt Outside-Clicks (schließt das Panel). -->
        <!-- svelte-ignore a11y_click_events_have_key_events -->
        <!-- svelte-ignore a11y_no_static_element_interactions -->
        <div class="auth-backdrop" onclick={() => (settingsOpen = false)}></div>
        <!-- svelte-ignore a11y_click_events_have_key_events -->
        <!-- svelte-ignore a11y_interactive_supports_focus -->
        <div class="auth-panel settings-panel" onclick={(e) => e.stopPropagation()} role="dialog" aria-label="Verbindung & Workspace">
          <div class="section-label">Connection</div>
          <label class="auth-label" for="trame-baseurl">Base URL</label>
          <input
            id="trame-baseurl"
            type="text"
            bind:value={baseUrlInput}
            onkeydown={(e) => e.key === 'Enter' && applyConnection()}
            placeholder="/ (Same-Origin) oder https://host:port/"
            spellcheck={false}
          />
          <label class="auth-label" for="trame-apipath">API-Pfad</label>
          <input
            id="trame-apipath"
            type="text"
            bind:value={apiPathInput}
            onkeydown={(e) => e.key === 'Enter' && applyConnection()}
            placeholder="api/trame"
            spellcheck={false}
          />
          {#if endpointState.isCustom}
            <p class="hint">Standalone-/Cross-Origin: der Zielserver muss CORS für diese DevUI-Origin freigeben.</p>
          {/if}
          <div class="auth-actions">
            <button class="ghost small" onclick={resetConnection} title="Auf Same-Origin-Default zurücksetzen">Reset</button>
            <button class="ghost small primary" onclick={applyConnection}>Apply</button>
          </div>

          <div class="section-label section-divider">Workspace</div>
          <div class="auth-actions">
            <button class="ghost small" onclick={handleExport} disabled={tabState.tabs.length === 0} title="Verbindung + Tabs + Theme + Layout + History als JSON exportieren (ohne Bearer)">Export</button>
            <button class="ghost small" onclick={handleImportClick} title="Workspace-JSON importieren">Import</button>
          </div>
          <input
            class="hidden-file"
            type="file"
            accept="application/json,.json"
            bind:this={fileInput}
            onchange={handleImportFile}
          />
          {#if importError}
            <p class="import-error">{importError}</p>
          {/if}
        </div>
      {/if}
    </div>

    <div class="dropdown-wrap">
      <button
        class="ghost small"
        class:active={authState.hasToken}
        onclick={toggleAuth}
        title="Bearer-Token für Auth"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4"></path>
        </svg>
        Auth
        {#if authState.hasToken}<span class="auth-dot" title="Bearer gesetzt"></span>{/if}
      </button>

      {#if authOpen}
        <!-- Backdrop fängt Outside-Clicks (schließt das Panel), ohne manuelle
             Listener-Cleanup. Panel liegt per z-index über dem Backdrop. -->
        <!-- svelte-ignore a11y_click_events_have_key_events -->
        <!-- svelte-ignore a11y_no_static_element_interactions -->
        <div class="auth-backdrop" onclick={() => (authOpen = false)}></div>
        <!-- svelte-ignore a11y_click_events_have_key_events -->
        <!-- svelte-ignore a11y_interactive_supports_focus -->
        <div class="auth-panel" onclick={(e) => e.stopPropagation()} role="dialog" aria-label="Bearer-Token">
          <label class="auth-label" for="trame-bearer-input">Bearer-Token</label>
          <input
            id="trame-bearer-input"
            type="password"
            bind:value={tokenInput}
            onkeydown={(e) => e.key === 'Enter' && applyToken()}
            placeholder="z. B. eyJhbGciOi…"
            autocomplete="off"
            spellcheck={false}
          />
          <div class="auth-actions">
            <button class="ghost small primary" onclick={applyToken} disabled={tokenInput.trim().length === 0}>Apply</button>
            <button class="ghost small" onclick={clearToken} disabled={!authState.hasToken}>Clear</button>
          </div>
        </div>
      {/if}
    </div>

    <button class="ghost small" onclick={() => tabState.createCodegenTab()} title="Generate client code">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <polyline points="16 18 22 12 16 6"></polyline>
        <polyline points="8 6 2 12 8 18"></polyline>
      </svg>
      Codegen
    </button>

    <button class="ghost small" onclick={() => tabState.createDependencyTab()} title="Dependency Builder — @alias-Batch visuell zusammenstellen">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <circle cx="6" cy="6" r="2"></circle>
        <circle cx="18" cy="6" r="2"></circle>
        <circle cx="12" cy="18" r="2"></circle>
        <line x1="7.41" y1="7.41" x2="10.59" y2="14.59"></line>
        <line x1="16.59" y1="7.41" x2="13.41" y2="14.59"></line>
      </svg>
      Dependency Builder
    </button>

    <button class="ghost small" onclick={() => historyState.toggle()} title="History">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <circle cx="12" cy="12" r="10"></circle>
        <polyline points="12 6 12 12 16 14"></polyline>
      </svg>
      History
    </button>

    <button class="ghost small" onclick={() => discoveryState.fetchDiscovery()} disabled={discoveryState.loading} title="Refresh Discovery">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <polyline points="23 4 23 10 17 10"></polyline>
        <polyline points="1 20 1 14 7 14"></polyline>
        <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"></path>
      </svg>
      {discoveryState.loading ? 'Loading...' : 'Refresh'}
    </button>

    <button class="ghost small icon" onclick={() => themeState.toggle()} title="Toggle theme">
      {#if themeState.theme === 'dark'}
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <circle cx="12" cy="12" r="5"></circle>
          <line x1="12" y1="1" x2="12" y2="3"></line>
          <line x1="12" y1="21" x2="12" y2="23"></line>
          <line x1="4.22" y1="4.22" x2="5.64" y2="5.64"></line>
          <line x1="18.36" y1="18.36" x2="19.78" y2="19.78"></line>
          <line x1="1" y1="12" x2="3" y2="12"></line>
          <line x1="21" y1="12" x2="23" y2="12"></line>
          <line x1="4.22" y1="19.78" x2="5.64" y2="18.36"></line>
          <line x1="18.36" y1="5.64" x2="19.78" y2="4.22"></line>
        </svg>
      {:else}
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"></path>
        </svg>
      {/if}
    </button>

    <a class="ghost small" href="/swagger/index.html" target="_blank">Swagger</a>
  </div>
</header>

<svelte:window onkeydown={(e) => { if (e.key === 'Escape') { if (authOpen) authOpen = false; if (settingsOpen) settingsOpen = false; } }} />

<style>
  .topbar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 8px 16px;
    background: var(--bg-elevated);
    border-bottom: 1px solid var(--border);
    flex-shrink: 0;
    z-index: 100;
  }
  .brand {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .brand-mark {
    flex-shrink: 0;
  }
  .title {
    font-weight: 700;
    font-size: 1rem;
    letter-spacing: -0.3px;
  }
  .actions {
    display: flex;
    gap: 6px;
    align-items: center;
  }

  /* Dropdowns (Auth + Settings) */
  .dropdown-wrap {
    position: relative;
    display: inline-flex;
  }
  .auth-dot {
    display: inline-block;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--success);
    margin-left: 2px;
    box-shadow: 0 0 0 2px var(--bg-elevated);
  }
  .ghost.small.active {
    color: var(--success);
  }
  .auth-backdrop {
    position: fixed;
    inset: 0;
    z-index: 200;
  }
  .auth-panel {
    position: absolute;
    top: calc(100% + 6px);
    right: 0;
    z-index: 201;
    min-width: 260px;
    padding: 12px;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .settings-panel {
    min-width: 280px;
  }
  .section-label {
    font-size: 0.7rem;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--text-dim);
    font-weight: 700;
  }
  .section-divider {
    margin-top: 4px;
    padding-top: 8px;
    border-top: 1px solid var(--border);
  }
  .auth-label {
    font-size: 0.75rem;
    color: var(--text-dim);
    font-weight: 600;
  }
  .auth-panel input {
    width: 100%;
    padding: 6px 8px;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text);
    font-size: 0.8rem;
    font-family: var(--font-mono, monospace);
    box-sizing: border-box;
  }
  .auth-panel input:focus {
    outline: none;
    border-color: var(--accent-secondary);
  }
  .auth-actions {
    display: flex;
    gap: 6px;
    justify-content: flex-end;
  }
  .ghost.small.primary {
    background: var(--accent-secondary);
    color: var(--bg-elevated);
    border-color: var(--accent-secondary);
  }
  .ghost.small.primary:disabled,
  .ghost.small:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
  .hidden-file {
    display: none;
  }
  .hint {
    font-size: 0.7rem;
    color: var(--text-dim);
    line-height: 1.4;
    margin: 0;
  }
  .import-error {
    font-size: 0.72rem;
    color: #ef4444;
    margin: 0;
  }
</style>