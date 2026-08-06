<script lang="ts">
  import type { Notification } from '../api';

  interface Props {
    notification: Notification;
    onRead?: (id: number) => void;
  }

  let { notification, onRead }: Props = $props();

  const typeIcons: Record<Notification['type'], string> = {
    Mail: '✉️',
    WhatsApp: '💬',
    Inbox: '📥'
  };
</script>

<div class="card" class:unread={!notification.isRead}>
  <div class="icon">{typeIcons[notification.type]}</div>
  <div class="body">
    <div class="meta">
      <span class="type">{notification.type}</span>
      <span class="sender">{notification.sender}</span>
      <span class="time">{new Date(notification.timestamp).toLocaleString()}</span>
    </div>
    <h3>{notification.title}</h3>
    <p>{notification.body}</p>
  </div>
  {#if !notification.isRead}
    <button class="read-btn" onclick={() => onRead?.(notification.id)}>Mark as read</button>
  {/if}
</div>

<style>
  .card {
    display: flex;
    gap: 1rem;
    align-items: flex-start;
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    padding: 1rem;
    margin-bottom: 0.75rem;
    transition: box-shadow 0.15s;
  }
  .card.unread {
    border-left: 4px solid #2563eb;
  }
  .icon {
    font-size: 1.5rem;
    line-height: 1;
  }
  .body {
    flex: 1;
  }
  .meta {
    display: flex;
    gap: 0.75rem;
    font-size: 0.75rem;
    color: #6b7280;
    margin-bottom: 0.25rem;
    flex-wrap: wrap;
  }
  .type {
    font-weight: 600;
    color: #2563eb;
  }
  h3 {
    margin: 0.25rem 0;
    font-size: 1rem;
  }
  p {
    margin: 0;
    color: #4b5563;
  }
  .read-btn {
    background: #eff6ff;
    border: 1px solid #bfdbfe;
    color: #1d4ed8;
    padding: 0.4rem 0.75rem;
    border-radius: 0.5rem;
    cursor: pointer;
    font-size: 0.875rem;
    white-space: nowrap;
  }
  .read-btn:hover {
    background: #dbeafe;
  }
</style>
