<script lang="ts">
  import { onMount } from 'svelte';
  import { chatState, setError, clearError, setLoading } from '../stores.svelte';
  import * as api from '../api';
  import ChatDetail from './ChatDetail.svelte';

  let newName = $state('');
  let newParticipants = $state('');

  onMount(async () => {
    await loadChats();
  });

  async function loadChats() {
    setLoading(true);
    clearError();
    try {
      chatState.chats = await api.getChats();
    } catch (e) {
      setError(`Chats could not be loaded: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }

  async function createChat() {
    if (!newName.trim()) return;
    const parts = newParticipants.split(',').map((p) => p.trim()).filter(Boolean);
    try {
      const chat = await api.createChat(newName, parts.length ? parts : ['You']);
      chatState.chats = [chat, ...chatState.chats];
      chatState.activeChatId = chat.id;
      newName = '';
      newParticipants = '';
    } catch (e) {
      setError(`Chat could not be created: ${(e as Error).message}`);
    }
  }

  function openChat(id: number) {
    chatState.activeChatId = id;
  }
</script>

{#if chatState.activeChatId}
  <ChatDetail />
{:else}
  <div class="chats">
    <div class="header">
      <h2>Chats</h2>
      <button onclick={loadChats}>🔄</button>
    </div>

    <div class="create">
      <input type="text" placeholder="Chat name" bind:value={newName} />
      <input type="text" placeholder="Participants (comma separated)" bind:value={newParticipants} />
      <button onclick={createChat} disabled={!newName.trim()}>New chat</button>
    </div>

    {#each chatState.chats as chat}
      <button class="chat-row" onclick={() => openChat(chat.id)}>
        <div class="chat-name">{chat.name}</div>
        <div class="chat-meta">{chat.participants.join(', ')} · {new Date(chat.lastMessageAt).toLocaleString()}</div>
      </button>
    {:else}
      <p class="empty">No chats yet.</p>
    {/each}
  </div>
{/if}

<style>
  .chats h2 {
    margin-top: 0;
  }
  .header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
  }
  .create {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1rem;
    flex-wrap: wrap;
  }
  .create input {
    flex: 1;
    min-width: 180px;
    padding: 0.5rem;
    border: 1px solid #d1d5db;
    border-radius: 0.5rem;
  }
  .create button,
  .header button {
    background: #2563eb;
    color: white;
    border: none;
    padding: 0.5rem 0.75rem;
    border-radius: 0.5rem;
    cursor: pointer;
  }
  .create button:disabled {
    background: #9ca3af;
    cursor: not-allowed;
  }
  .chat-row {
    display: block;
    width: 100%;
    text-align: left;
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    padding: 1rem;
    margin-bottom: 0.75rem;
    cursor: pointer;
    transition: box-shadow 0.15s;
  }
  .chat-row:hover {
    box-shadow: 0 4px 6px -1px rgb(0 0 0 / 0.1);
  }
  .chat-name {
    font-weight: 600;
    margin-bottom: 0.25rem;
  }
  .chat-meta {
    color: #6b7280;
    font-size: 0.875rem;
  }
  .empty {
    color: #6b7280;
    font-style: italic;
  }
</style>
