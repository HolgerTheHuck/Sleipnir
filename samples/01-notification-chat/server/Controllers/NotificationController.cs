using SleipnirCore.Attributes;
using Sleipnir.Samples.NotificationChat.Server.Data;
using Sleipnir.Samples.NotificationChat.Server.Models;

namespace Sleipnir.Samples.NotificationChat.Server.Controllers;

[SleipnirController("Notification")]
public class NotificationController(INotificationStore store)
{
    [SleipnirMethod("GetInbox")]
    public IReadOnlyList<Notification> GetInbox()
        => store.GetInbox();

    [SleipnirMethod("GetByType")]
    public IReadOnlyList<Notification> GetByType(NotificationType type)
        => store.GetByType(type);

    [SleipnirMethod("GetUnreadCount")]
    public int GetUnreadCount()
        => store.GetUnreadCount();

    [SleipnirMethod("GetById")]
    public Notification? GetById(int id)
        => store.GetNotification(id);

    [SleipnirMethod("SendMail")]
    public Notification SendMail(string to, string subject, string body)
    {
        var notification = new Notification
        {
            Type = NotificationType.Mail,
            Title = subject,
            Body = body,
            Sender = to,
            IsRead = false
        };
        return store.AddNotification(notification);
    }

    [SleipnirMethod("SendWhatsApp")]
    public Notification SendWhatsApp(string to, string text)
    {
        var notification = new Notification
        {
            Type = NotificationType.WhatsApp,
            Title = "WhatsApp from " + to,
            Body = text,
            Sender = to,
            IsRead = false
        };
        return store.AddNotification(notification);
    }

    [SleipnirMethod("SendInbox")]
    public Notification SendInbox(string title, string body)
    {
        var notification = new Notification
        {
            Type = NotificationType.Inbox,
            Title = title,
            Body = body,
            Sender = "system",
            IsRead = false
        };
        return store.AddNotification(notification);
    }

    [SleipnirMethod("MarkAsRead")]
    public object MarkAsRead(int id)
    {
        var ok = store.MarkAsRead(id);
        return ok
            ? new { success = true, id }
            : new { success = false, error = $"Notification {id} not found" };
    }
}
