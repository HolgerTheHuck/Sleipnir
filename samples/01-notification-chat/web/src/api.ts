import { SleipnirRestClient, SleipnirCall, SleipnirWebSocketClient } from 'sleipnir-client';
import type { SleipnirResponse } from 'sleipnir-client';

export const rest = new SleipnirRestClient('/');

let ws: SleipnirWebSocketClient | null = null;

export async function ensureWs(): Promise<SleipnirWebSocketClient> {
  if (!ws) {
    ws = new SleipnirWebSocketClient('/', { wsPath: 'sleipnirws' });
    await ws.connect();
  }
  return ws;
}

export async function closeWs(): Promise<void> {
  ws?.close();
  ws = null;
}

export interface Notification {
  id: number;
  type: 'Mail' | 'WhatsApp' | 'Inbox';
  title: string;
  body: string;
  sender: string;
  isRead: boolean;
  timestamp: string;
}

export interface Chat {
  id: number;
  name: string;
  participants: string[];
  lastMessageAt: string;
}

export interface MediaAttachment {
  id: number;
  url: string;
  mimeType: string;
  sizeBytes: number;
  thumbnailUrl?: string;
  mediaType: 'Image' | 'Video';
}

export interface Message {
  id: number;
  chatId: number;
  sender: string;
  text: string;
  timestamp: string;
  attachments: MediaAttachment[];
}

export interface MediaItem {
  id: number;
  url: string;
  mimeType: string;
  sizeBytes: number;
  thumbnailUrl?: string;
  mediaType: 'Image' | 'Video';
  uploadedAt: string;
}

export async function getInbox(): Promise<Notification[]> {
  return rest.callJson<Notification[]>('Notification', 'GetInbox');
}

export async function getUnreadCount(): Promise<number> {
  return rest.callJson<number>('Notification', 'GetUnreadCount');
}

export async function getByType(type: Notification['type']): Promise<Notification[]> {
  return rest.callJson<Notification[]>('Notification', 'GetByType', { type });
}

export async function markAsRead(id: number): Promise<SleipnirResponse> {
  return rest.call(SleipnirCall.init('Notification', 'MarkAsRead').with({ id }).toRequest());
}

export async function sendMail(to: string, subject: string, body: string): Promise<Notification> {
  return rest.callJson<Notification>('Notification', 'SendMail', { to, subject, body });
}

export async function sendWhatsApp(to: string, text: string): Promise<Notification> {
  return rest.callJson<Notification>('Notification', 'SendWhatsApp', { to, text });
}

export async function sendInbox(title: string, body: string): Promise<Notification> {
  return rest.callJson<Notification>('Notification', 'SendInbox', { title, body });
}

export async function getChats(): Promise<Chat[]> {
  return rest.callJson<Chat[]>('Chat', 'GetChats');
}

export async function getMessages(chatId: number): Promise<Message[]> {
  return rest.callJson<Message[]>('Chat', 'GetMessages', { chatId });
}

export async function sendMessage(chatId: number, sender: string, text: string): Promise<Message> {
  return rest.callJson<Message>('Chat', 'SendMessage', { chatId, sender, text });
}

export async function createChat(name: string, participants: string[]): Promise<Chat> {
  return rest.callJson<Chat>('Chat', 'CreateChat', { name, participants });
}

export async function getGallery(): Promise<MediaItem[]> {
  return rest.callJson<MediaItem[]>('Media', 'GetGallery');
}

export async function uploadImage(url: string, mimeType: string, sizeBytes: number, thumbnailUrl?: string): Promise<MediaItem> {
  return rest.callJson<MediaItem>('Media', 'UploadImage', { url, mimeType, sizeBytes, thumbnailUrl });
}

export async function uploadVideo(url: string, mimeType: string, sizeBytes: number, thumbnailUrl?: string): Promise<MediaItem> {
  return rest.callJson<MediaItem>('Media', 'UploadVideo', { url, mimeType, sizeBytes, thumbnailUrl });
}

export async function loadDashboardBatch(): Promise<{
  unread: number;
  chats: Chat[];
  gallery: MediaItem[];
}> {
  const res = await rest.callBatch([
    SleipnirCall.init('Notification', 'GetUnreadCount').named('unread').toRequest(),
    SleipnirCall.init('Chat', 'GetChats').named('chats').toRequest(),
    SleipnirCall.init('Media', 'GetGallery').named('gallery').toRequest()
  ]);

  const byId = new Map(res.map((r) => [r.id, r.data]));
  return {
    unread: (byId.get('unread') as number) ?? 0,
    chats: (byId.get('chats') as Chat[]) ?? [],
    gallery: (byId.get('gallery') as MediaItem[]) ?? []
  };
}
