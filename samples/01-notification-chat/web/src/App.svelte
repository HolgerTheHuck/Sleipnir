<script lang="ts">
  import { appState } from './stores.svelte';
  import Dashboard from './views/Dashboard.svelte';
  import Inbox from './views/Inbox.svelte';
  import Chats from './views/Chats.svelte';
  import Gallery from './views/Gallery.svelte';

  const views = {
    dashboard: Dashboard,
    inbox: Inbox,
    chats: Chats,
    gallery: Gallery
  };

  function nav(view: keyof typeof views) {
    appState.view = view;
  }
</script>

<div class="layout">
  <aside class="sidebar">
    <h1>Notification Chat</h1>
    <nav>
      <button class:active={appState.view === 'dashboard'} onclick={() => nav('dashboard')}>Dashboard</button>
      <button class:active={appState.view === 'inbox'} onclick={() => nav('inbox')}>Inbox</button>
      <button class:active={appState.view === 'chats'} onclick={() => nav('chats')}>Chats</button>
      <button class:active={appState.view === 'gallery'} onclick={() => nav('gallery')}>Gallery</button>
    </nav>
    <footer>
      <a href="/Sleipnir" target="_blank">Sleipnir DevUI ↗</a>
    </footer>
  </aside>

  <main class="content">
    {#if appState.error}
      <div class="error-banner">{appState.error}</div>
    {/if}
    {#if appState.loading}
      <div class="spinner">Loading…</div>
    {/if}
    <svelte:component this={views[appState.view]} />
  </main>
</div>

<style>
  :global(*) {
    box-sizing: border-box;
  }
  :global(body) {
    margin: 0;
    font-family: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    background: #f6f7f9;
    color: #1f2937;
  }
  .layout {
    display: grid;
    grid-template-columns: 220px 1fr;
    min-height: 100vh;
  }
  .sidebar {
    background: #111827;
    color: white;
    padding: 1rem;
    display: flex;
    flex-direction: column;
  }
  .sidebar h1 {
    font-size: 1.25rem;
    margin: 0 0 1rem;
  }
  .sidebar nav {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    flex: 1;
  }
  .sidebar button {
    background: transparent;
    border: none;
    color: #d1d5db;
    text-align: left;
    padding: 0.75rem 1rem;
    border-radius: 0.5rem;
    cursor: pointer;
    font-size: 1rem;
  }
  .sidebar button:hover,
  .sidebar button.active {
    background: #2563eb;
    color: white;
  }
  .sidebar footer {
    margin-top: auto;
    font-size: 0.875rem;
  }
  .sidebar a {
    color: #93c5fd;
    text-decoration: none;
  }
  .content {
    padding: 1.5rem;
    overflow-y: auto;
  }
  .error-banner {
    background: #fee2e2;
    color: #991b1b;
    padding: 0.75rem 1rem;
    border-radius: 0.5rem;
    margin-bottom: 1rem;
  }
  .spinner {
    color: #6b7280;
    margin-bottom: 1rem;
  }
</style>
