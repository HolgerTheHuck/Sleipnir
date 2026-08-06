import type { Notification, Chat, Message, MediaItem } from './api';

export const appState = $state({
  view: 'dashboard' as 'dashboard' | 'inbox' | 'chats' | 'gallery',
  loading: false,
  error: ''
});

export const notificationState = $state({
  inbox: [] as Notification[],
  unreadCount: 0,
  selectedType: 'All' as 'All' | Notification['type']
});

export const chatState = $state({
  chats: [] as Chat[],
  activeChatId: null as number | null,
  messages: [] as Message[]
});

export const mediaState = $state({
  gallery: [] as MediaItem[]
});

export function setError(msg: string) {
  appState.error = msg;
  console.error(msg);
}

export function clearError() {
  appState.error = '';
}

export function setLoading(value: boolean) {
  appState.loading = value;
}
