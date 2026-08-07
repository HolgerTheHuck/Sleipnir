using System.Collections.Concurrent;
using Trame.Samples.NotificationChat.Server.Models;

namespace Trame.Samples.NotificationChat.Server.Data;

public sealed class NotificationStore : INotificationStore
{
    private int _nextNotificationId;
    private int _nextChatId;
    private int _nextMessageId;
    private int _nextMediaId;

    private readonly ConcurrentDictionary<int, Notification> _notifications = new();
    private readonly ConcurrentDictionary<int, Chat> _chats = new();
    private readonly ConcurrentDictionary<int, Message> _messages = new();
    private readonly ConcurrentDictionary<int, MediaItem> _media = new();

    public NotificationStore()
    {
        Seed();
    }

    private void Seed()
    {
        AddNotification(new Notification
        {
            Type = NotificationType.Mail,
            Title = "Welcome to Trame",
            Body = "Your notification chat sample is ready.",
            Sender = "team@trame.test",
            IsRead = false
        });

        AddNotification(new Notification
        {
            Type = NotificationType.WhatsApp,
            Title = "New WhatsApp",
            Body = "Have you seen the pictures?",
            Sender = "+491511234567",
            IsRead = false
        });

        AddNotification(new Notification
        {
            Type = NotificationType.Inbox,
            Title = "System",
            Body = "Inbox sample message.",
            Sender = "system",
            IsRead = true
        });

        var chat = AddChat(new Chat
        {
            Name = "Trame Team",
            Participants = new List<string> { "Alice", "Bob" }
        });

        AddMessage(new Message
        {
            ChatId = chat.Id,
            Sender = "Alice",
            Text = "Hi! Here is a picture from the server."
        });

        AddMedia(new MediaItem
        {
            Url = "https://picsum.photos/id/10/800/600",
            MimeType = "image/jpeg",
            SizeBytes = 124_000,
            MediaType = MediaType.Image,
            ThumbnailUrl = "https://picsum.photos/id/10/200/150"
        });

        AddMedia(new MediaItem
        {
            Url = "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4",
            MimeType = "video/mp4",
            SizeBytes = 5_000_000,
            MediaType = MediaType.Video,
            ThumbnailUrl = "https://picsum.photos/id/20/400/225"
        });
    }

    public Notification AddNotification(Notification n)
    {
        n.Id = Interlocked.Increment(ref _nextNotificationId);
        _notifications[n.Id] = n;
        return n;
    }

    public Notification? GetNotification(int id)
        => _notifications.TryGetValue(id, out var n) ? n : null;

    public IReadOnlyList<Notification> GetInbox()
        => _notifications.Values.OrderByDescending(n => n.Timestamp).ToList();

    public IReadOnlyList<Notification> GetByType(NotificationType type)
        => _notifications.Values.Where(n => n.Type == type).OrderByDescending(n => n.Timestamp).ToList();

    public int GetUnreadCount()
        => _notifications.Values.Count(n => !n.IsRead);

    public bool MarkAsRead(int id)
    {
        if (!_notifications.TryGetValue(id, out var n)) return false;
        n.IsRead = true;
        return true;
    }

    public Chat AddChat(Chat c)
    {
        c.Id = Interlocked.Increment(ref _nextChatId);
        _chats[c.Id] = c;
        return c;
    }

    public Chat? GetChat(int id)
        => _chats.TryGetValue(id, out var c) ? c : null;

    public IReadOnlyList<Chat> GetChats()
        => _chats.Values.OrderByDescending(c => c.LastMessageAt).ToList();

    public Message AddMessage(Message m)
    {
        m.Id = Interlocked.Increment(ref _nextMessageId);
        m.Timestamp = DateTime.UtcNow;
        _messages[m.Id] = m;

        if (_chats.TryGetValue(m.ChatId, out var chat))
        {
            chat.LastMessageAt = m.Timestamp;
        }

        return m;
    }

    public IReadOnlyList<Message> GetMessagesByChat(int chatId)
        => _messages.Values.Where(m => m.ChatId == chatId).OrderBy(m => m.Timestamp).ToList();

    public MediaItem AddMedia(MediaItem item)
    {
        item.Id = Interlocked.Increment(ref _nextMediaId);
        item.UploadedAt = DateTime.UtcNow;
        _media[item.Id] = item;
        return item;
    }

    public MediaItem? GetMedia(int id)
        => _media.TryGetValue(id, out var m) ? m : null;

    public IReadOnlyList<MediaItem> GetGallery()
        => _media.Values.OrderByDescending(m => m.UploadedAt).ToList();
}
