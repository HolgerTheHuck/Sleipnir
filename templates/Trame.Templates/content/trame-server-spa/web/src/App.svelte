<script lang="ts">
  import { hello, ping } from './api.js';

  let name = $state('Trame');
  let message = $state('');
  let serverTime = $state('');
  let busy = $state(false);

  async function greet() {
    busy = true;
    try {
      message = await hello(name);
      const p = await ping();
      serverTime = p.time;
    } finally {
      busy = false;
    }
  }
</script>

<main>
  <h1>Trame + Svelte 5</h1>
  <p>
    Server says:
    <strong>{message || '…'}</strong>
  </p>
  {#if serverTime}
    <p>Server time: {new Date(serverTime).toLocaleString()}</p>
  {/if}
  <div class="row">
    <input bind:value={name} placeholder="Your name" />
    <button onclick={greet} disabled={busy}>{busy ? 'Calling…' : 'Greet'}</button>
  </div>
</main>

<style>
  main {
    font-family: system-ui, sans-serif;
    max-width: 480px;
    margin: 4rem auto;
    padding: 0 1rem;
  }
  .row {
    display: flex;
    gap: 0.5rem;
    margin-top: 1rem;
  }
  input {
    flex: 1;
    padding: 0.5rem;
  }
  button {
    padding: 0.5rem 1rem;
  }
</style>
