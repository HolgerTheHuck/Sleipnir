<script lang="ts">
  import type { MediaItem } from '../api';

  interface Props {
    item: MediaItem;
  }

  let { item }: Props = $props();

  function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
</script>

<div class="thumb">
  {#if item.thumbnailUrl}
    <img src={item.thumbnailUrl} alt={item.mediaType} />
  {:else}
    <div class="placeholder">{item.mediaType === 'Image' ? '🖼️' : '🎬'}</div>
  {/if}
  <div class="info">
    <span class="type">{item.mediaType}</span>
    <span class="size">{formatBytes(item.sizeBytes)}</span>
    <span class="mime">{item.mimeType}</span>
  </div>
  <a href={item.url} target="_blank" rel="noopener noreferrer">Open ↗</a>
</div>

<style>
  .thumb {
    background: white;
    border: 1px solid #e5e7eb;
    border-radius: 0.75rem;
    overflow: hidden;
    display: flex;
    flex-direction: column;
  }
  .thumb img,
  .placeholder {
    width: 100%;
    height: 160px;
    object-fit: cover;
    background: #f3f4f6;
    display: grid;
    place-items: center;
    font-size: 2rem;
  }
  .info {
    padding: 0.75rem;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    font-size: 0.875rem;
  }
  .type {
    font-weight: 600;
    color: #2563eb;
    text-transform: uppercase;
  }
  .size,
  .mime {
    color: #6b7280;
  }
  a {
    margin: 0 0.75rem 0.75rem;
    color: #2563eb;
    text-decoration: none;
    font-size: 0.875rem;
  }
  a:hover {
    text-decoration: underline;
  }
</style>
