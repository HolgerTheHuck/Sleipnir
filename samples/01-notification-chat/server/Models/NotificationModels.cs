using System.Text.Json.Serialization;

namespace Trame.Samples.NotificationChat.Server.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationType
{
    Mail,
    WhatsApp,
    Inbox
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaType
{
    Image,
    Video
}

public class Notification
{
    public int Id { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class Contact
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
}

public class Chat
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Participants { get; set; } = new();
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
}

public class MediaAttachment
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? ThumbnailUrl { get; set; }
    public MediaType MediaType { get; set; }
}

public class Message
{
    public int Id { get; set; }
    public int ChatId { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<MediaAttachment> Attachments { get; set; } = new();
}

public class MediaItem
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? ThumbnailUrl { get; set; }
    public MediaType MediaType { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
