<script lang="ts">
  import { onMount } from 'svelte';
  import { chatState, setError, clearError, setLoading } from '../stores.svelte';
  import * as api from '../api';
  import MessageBubble from '../components/MessageBubble.svelte';

  const chat = $derived(chatState.chats.find((c) => c.id === chatState.activeChatId)!);
  const messages = $derived(chatState.messages);

  let text = $state('');
  let sender = $state('Me');

  onMount(async () => {
    await loadMessages();
  });

  async function loadMessages() {
    if (!chatState.activeChatId) return;
    setLoading(true);
    clearError();
    try {
      chatState.messages = await api.getMessages(chatState.activeChatId);
    } catch (e) {
      setError(`Messages could not be loaded: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }

  async function send() {
    if (!text.trim() || !chatState.activeChatId) return;
    setLoading(true);
    try {
      const msg = await api.sendMessage(chatState.activeChatId, sender, text);
      chatState.messages = [...chatState.messages, msg];
      text = '';
    } catch (e) {
      setError(`Message could not be sent: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }
</script>

<div class="detail">
  <div class="header">
    <button class="back" onclick={() => (chatState.activeChatId = null)}>← Back</button>
    <h2>{chat?.name}</h2>
    <span class="participants">{chat?.participants.join(', ')}</span>
  </div>

  <div class="messages">
    {#each messages as msg}
      <MessageBubble {msg} me={sender} />
    {:else}
      <p class="empty">No messages yet.</p>
    {/each}
  </div>

  <div class="composer">
    <input type="text" placeholder="Name" bind:value={sender} />
    <input type="text" placeholder="Write a message…" bind:value={text} onkeydown={(e) => e.key === 'Enter' && send()} />
    <button onclick={send} disabled={!text.trim()}>Send</button>
  </div>
</div>

<style>
  .detail {
    display: flex;
    flex-direction: column;
    height: calc(100vh - 3rem);
  }
  .header {
    display: flex;
    align-items: baseline;
    gap: 1rem;
    margin-bottom: 1rem;
    flex-wrap: wrap;
  }
  .header h2 {
    margin: 0;
  }
  .back {
    background: #e5e7eb;
    border: none;
    padding: 0.4rem 0.75rem;
    border-radius: 0.5rem;
    cursor: pointer;
  }
  .participants {
    color: #6b7280;
    font-size: 0.875rem;
  }
  .messages {
    flex: 1;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    padding: 1rem;
    margin-bottom: 1rem;
  }
  .empty {
    color: #6b7280;
    font-style: italic;
  }
  .composer {
    display: flex;
    gap: 0.5rem;
  }
  .composer input {
    flex: 1;
    padding: 0.6rem;
    border: 1px solid #d1d5db;
    border-radius: 0.5rem;
  }
  .composer input:first-child {
    flex: 0 0 120px;
  }
  .composer button {
    background: #2563eb;
    color: white;
    border: none;
    padding: 0 1rem;
    border-radius: 0.5rem;
    cursor: pointer;
  }
  .composer button:disabled {
    background: #9ca3af;
    cursor: not-allowed;
  }
</style>
