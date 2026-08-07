using TrameCore.Attributes;
using Trame.Samples.NotificationChat.Server.Data;
using Trame.Samples.NotificationChat.Server.Models;

namespace Trame.Samples.NotificationChat.Server.Controllers;

[TrameController("Notification")]
public class NotificationController(INotificationStore store)
{
    [TrameMethod("GetInbox")]
    public IReadOnlyList<Notification> GetInbox()
        => store.GetInbox();

    [TrameMethod("GetByType")]
    public IReadOnlyList<Notification> GetByType(NotificationType type)
        => store.GetByType(type);

    [TrameMethod("GetUnreadCount")]
    public int GetUnreadCount()
        => store.GetUnreadCount();

    [TrameMethod("GetById")]
    public Notification? GetById(int id)
        => store.GetNotification(id);

    [TrameMethod("SendMail")]
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

    [TrameMethod("SendWhatsApp")]
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

    [TrameMethod("SendInbox")]
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

    [TrameMethod("MarkAsRead")]
    public object MarkAsRead(int id)
    {
        var ok = store.MarkAsRead(id);
        return ok
            ? new { success = true, id }
            : new { success = false, error = $"Notification {id} not found" };
    }
}
