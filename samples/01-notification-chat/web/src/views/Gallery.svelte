<script lang="ts">
  import { onMount } from 'svelte';
  import { mediaState, setError, clearError, setLoading } from '../stores.svelte';
  import * as api from '../api';
  import MediaThumb from '../components/MediaThumb.svelte';

  let newUrl = $state('');
  let newType = $state<'Image' | 'Video'>('Image');
  let newSize = $state(100_000);

  onMount(async () => {
    await loadGallery();
  });

  async function loadGallery() {
    setLoading(true);
    clearError();
    try {
      mediaState.gallery = await api.getGallery();
    } catch (e) {
      setError(`Gallery could not be loaded: ${(e as Error).message}`);
    } finally {
      setLoading(false);
    }
  }

  async function addMedia() {
    if (!newUrl.trim()) return;
    try {
      const item =
        newType === 'Image'
          ? await api.uploadImage(newUrl, 'image/jpeg', newSize, newUrl)
          : await api.uploadVideo(newUrl, 'video/mp4', newSize, undefined);
      mediaState.gallery = [item, ...mediaState.gallery];
      newUrl = '';
    } catch (e) {
      setError(`Media could not be added: ${(e as Error).message}`);
    }
  }

  const images = $derived(mediaState.gallery.filter((m) => m.mediaType === 'Image'));
  const videos = $derived(mediaState.gallery.filter((m) => m.mediaType === 'Video'));
</script>

<div class="gallery">
  <div class="header">
    <h2>Gallery</h2>
    <button onclick={loadGallery}>🔄</button>
  </div>

  <div class="add">
    <select bind:value={newType}>
      <option value="Image">Image</option>
      <option value="Video">Video</option>
    </select>
    <input type="text" placeholder="URL to media" bind:value={newUrl} />
    <input type="number" placeholder="Size in bytes" bind:value={newSize} />
    <button onclick={addMedia} disabled={!newUrl.trim()}>Add</button>
  </div>

  {#if images.length > 0}
    <h3>Images ({images.length})</h3>
    <div class="grid">
      {#each images as item}
        <MediaThumb {item} />
      {/each}
    </div>
  {/if}

  {#if videos.length > 0}
    <h3>Videos ({videos.length})</h3>
    <div class="grid">
      {#each videos as item}
        <MediaThumb {item} />
      {/each}
    </div>
  {/if}

  {#if mediaState.gallery.length === 0}
    <p class="empty">No media yet.</p>
  {/if}
</div>

<style>
  .gallery h2 {
    margin-top: 0;
  }
  .header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
  }
  .add {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1.5rem;
    flex-wrap: wrap;
  }
  .add input[type='text'] {
    flex: 1;
    min-width: 200px;
  }
  .add input,
  .add select {
    padding: 0.5rem;
    border: 1px solid #d1d5db;
    border-radius: 0.5rem;
  }
  .add button,
  .header button {
    background: #2563eb;
    color: white;
    border: none;
    padding: 0.5rem 0.75rem;
    border-radius: 0.5rem;
    cursor: pointer;
  }
  .add button:disabled {
    background: #9ca3af;
    cursor: not-allowed;
  }
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
    gap: 1rem;
    margin-bottom: 1.5rem;
  }
  .empty {
    color: #6b7280;
    font-style: italic;
  }
</style>
