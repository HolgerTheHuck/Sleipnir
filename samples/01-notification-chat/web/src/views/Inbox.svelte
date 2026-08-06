<script lang="ts">
  import { onMount } from 'svelte';
  import { notificationState, setError, clearError, setLoading } from '../stores.svelte';
  import * as api from '../api';
  import NotificationCard from '../components/NotificationCard.svelte';

  onMount(async () => {
    await loadInbox();
  });

  async function loadInbox() {
    setLoading(true);
    clearError();
    try {
      const [inbox, unread] = await Promise.all([api.getInbox(), api.getUnreadCount()]);
      notificationState.inbox = inbox;
      notificationState.unreadCount = unread;
    } catch (e) {
      setError(`Inbox could not be loaded: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }

  async function markRead(id: number) {
    try {
      await api.markAsRead(id);
      const n = notificationState.inbox.find((x) => x.id === id);
      if (n) n.isRead = true;
      notificationState.unreadCount = Math.max(0, notificationState.unreadCount - 1);
    } catch (e) {
      setError(`Could not mark as read: ${(e as Error).message}`);
    }
  }

  async function sendTestNotification() {
    try {
      await api.sendInbox('Test from the SPA', 'This inbox message was created manually.');
      await loadInbox();
    } catch (e) {
      setError(`Test notification could not be sent: ${(e as Error).message}`);
    }
  }

  let filtered = $derived(
    notificationState.selectedType === 'All'
      ? notificationState.inbox
      : notificationState.inbox.filter((n) => n.type === notificationState.selectedType)
  );
</script>

<div class="inbox">
  <div class="header">
    <h2>Inbox</h2>
    <div class="toolbar">
      <select bind:value={notificationState.selectedType}>
        <option value="All">All ({notificationState.inbox.length})</option>
        <option value="Mail">Mail</option>
        <option value="WhatsApp">WhatsApp</option>
        <option value="Inbox">Inbox</option>
      </select>
      <button onclick={sendTestNotification}>+ Test notification</button>
      <button onclick={loadInbox}>🔄</button>
    </div>
  </div>

  <p class="stats">{notificationState.unreadCount} unread of {notificationState.inbox.length} items</p>

  {#each filtered as n}
    <NotificationCard notification={n} onRead={markRead} />
  {:else}
    <p class="empty">No notifications.</p>
  {/each}
</div>

<style>
  .inbox h2 {
    margin-top: 0;
  }
  .header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 1rem;
    flex-wrap: wrap;
  }
  .toolbar {
    display: flex;
    gap: 0.5rem;
    align-items: center;
  }
  select {
    padding: 0.4rem 0.6rem;
    border-radius: 0.5rem;
    border: 1px solid #d1d5db;
    background: white;
  }
  button {
    background: #2563eb;
    color: white;
    border: none;
    padding: 0.5rem 0.75rem;
    border-radius: 0.5rem;
    cursor: pointer;
  }
  button:hover {
    background: #1d4ed8;
  }
  .stats {
    color: #6b7280;
    font-size: 0.875rem;
  }
  .empty {
    color: #6b7280;
    font-style: italic;
  }
</style>
