<script lang="ts">
  import { onMount } from 'svelte';
  import { notificationState, chatState, mediaState, setError, clearError, setLoading } from '../stores.svelte';
  import * as api from '../api';

  onMount(async () => {
    setLoading(true);
    clearError();
    try {
      const data = await api.loadDashboardBatch();
      notificationState.unreadCount = data.unread;
      chatState.chats = data.chats;
      mediaState.gallery = data.gallery;
    } catch (e) {
      setError(`Dashboard could not be loaded: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  });

  async function sendDemoWhatsApp() {
    try {
      await api.sendWhatsApp('+491511111111', 'Hello from the Trame dashboard!');
      notificationState.unreadCount += 1;
    } catch (e) {
      setError(`WhatsApp could not be sent: ${(e as Error).message}`);
    }
  }

  async function sendDemoMail() {
    try {
      await api.sendMail('demo@trame.test', 'Trame test mail', 'This is a test mail from the Svelte SPA.');
      notificationState.unreadCount += 1;
    } catch (e) {
      setError(`Mail could not be sent: ${(e as Error).message}`);
    }
  }
</script>

<div class="dashboard">
  <h2>Dashboard</h2>

  <div class="cards">
    <div class="metric">
      <span class="value">{notificationState.unreadCount}</span>
      <span class="label">Unread</span>
    </div>
    <div class="metric">
      <span class="value">{chatState.chats.length}</span>
      <span class="label">Chats</span>
    </div>
    <div class="metric">
      <span class="value">{mediaState.gallery.length}</span>
      <span class="label">Media</span>
    </div>
  </div>

  <div class="actions">
    <button onclick={sendDemoWhatsApp}>📨 Send demo WhatsApp</button>
    <button onclick={sendDemoMail}>✉️ Send demo mail</button>
  </div>

  <h3>Latest media</h3>
  <div class="media-grid">
    {#each mediaState.gallery.slice(0, 4) as item}
      <img src={item.thumbnailUrl ?? item.url} alt="media" />
    {/each}
  </div>

  <p class="hint">
    All data comes from a single parallel Trame batch (Notification.GetUnreadCount + Chat.GetChats + Media.GetGallery).
  </p>
</div>

<style>
  .dashboard h2 {
    margin-top: 0;
  }
  .cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
    gap: 1rem;
    margin-bottom: 1.5rem;
  }
  .metric {
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    padding: 1.25rem;
    display: flex;
    flex-direction: column;
    align-items: center;
  }
  .value {
    font-size: 2rem;
    font-weight: 700;
    color: #2563eb;
  }
  .label {
    color: #6b7280;
    font-size: 0.875rem;
  }
  .actions {
    display: flex;
    gap: 0.75rem;
    margin-bottom: 1.5rem;
  }
  .actions button {
    background: #2563eb;
    color: white;
    border: none;
    padding: 0.6rem 1rem;
    border-radius: 0.5rem;
    cursor: pointer;
    font-size: 0.9rem;
  }
  .actions button:hover {
    background: #1d4ed8;
  }
  .media-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
    gap: 0.75rem;
  }
  .media-grid img {
    width: 100%;
    height: 120px;
    object-fit: cover;
    border-radius: 0.5rem;
  }
  .hint {
    color: #6b7280;
    font-size: 0.875rem;
    margin-top: 1.5rem;
  }
</style>
