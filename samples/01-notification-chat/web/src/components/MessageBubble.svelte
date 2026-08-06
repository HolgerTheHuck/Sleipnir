<script lang="ts">
  import type { Message } from '../api';

  interface Props {
    message: Message;
    me: string;
  }

  let { message, me }: Props = $props();

  const isMe = $derived(message.sender === me);
  const hasMedia = $derived(message.attachments.length > 0);
</script>

<div class="bubble" class:me={isMe}>
  <div class="sender">{message.sender}</div>
  <p class="text">{message.text}</p>

  {#if hasMedia}
    <div class="media">
      {#each message.attachments as a}
        {#if a.mediaType === 'Image'}
          <img src={a.url} alt="attachment" />
        {:else}
          <video src={a.url} controls preload="metadata">
            <track kind="captions" srclang="en" label="English" default />
          </video>
        {/if}
      {/each}
    </div>
  {/if}

  <span class="time">{new Date(message.timestamp).toLocaleTimeString()}</span>
</div>

<style>
  .bubble {
    max-width: 70%;
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    padding: 0.75rem 1rem;
    align-self: flex-start;
    margin-bottom: 0.75rem;
  }
  .bubble.me {
    background: #dbeafe;
    border-color: #bfdbfe;
    align-self: flex-end;
  }
  .sender {
    font-size: 0.75rem;
    font-weight: 600;
    color: #2563eb;
    margin-bottom: 0.25rem;
  }
  .text {
    margin: 0 0 0.25rem;
    line-height: 1.4;
  }
  .time {
    font-size: 0.7rem;
    color: #6b7280;
  }
  .media {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    margin: 0.5rem 0;
  }
  .media img,
  .media video {
    max-width: 100%;
    max-height: 240px;
    border-radius: 0.5rem;
    object-fit: cover;
  }
</style>
