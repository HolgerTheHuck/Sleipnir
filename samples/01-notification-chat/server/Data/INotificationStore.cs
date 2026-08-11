using Sleipnir.Samples.NotificationChat.Server.Models;

namespace Sleipnir.Samples.NotificationChat.Server.Data;

/// <summary>
/// Repository-Schnittstelle für die Notification-/Chat-/Message-/MediaItem-Domäne.
/// Phase 2 (North-Bound Secure Store) — der Service-Layer ist der Seam; diese
/// Schnittstelle ist die Abstraktion, die die Controller konsumieren. Die
/// in-memory <see cref="NotificationStore"/> ist die Default-Implementierung (Demo,
/// South-Bound). Für North-Bound/Produktion: eine EF-Core- (oder Dapper-)Implementation,
/// die <see cref="INotificationStore"/> gegen eine echte DB realisiert und in der DI
/// registriert (<c>AddScoped&lt;INotificationStore, EfNotificationStore&gt;()</c>).
/// </summary>
/// <remarks>
/// <para>
/// **Wichtig für North-Bound:** die Controller sind per-call scoped (Sleipnir resolved sie
/// pro Call via <c>IServiceScopeFactory.CreateScope</c>). Eine EF-basierte Implementation
/// sollte <c>Scoped</c> sein ( DbContext ist scoped), damit jeder Call einen eigenen
/// Scope bekommt — parallel-safe. Die in-memory Default bleibt <c>Singleton</c> (keine
/// echten Connections, <see cref="ConcurrentDictionary{TKey, TValue}"/> ist thread-safe).
/// </para>
/// <para>
/// Siehe <c>ROADMAP.md</c> Phase 2 und <c>BEST_PRACTICES.md</c> §1.3 (Controller lifetime
/// and DI). Das Muster: Design den Service einmal, expose ihn via Sleipnir — der Store ist
/// austauschbar unter derselben Schnittstelle.
/// </para>
/// </remarks>
public interface INotificationStore
{
    // ─── Notification ──────────────────────────────────────────────────────
    Notification AddNotification(Notification n);
    Notification? GetNotification(int id);
    IReadOnlyList<Notification> GetInbox();
    IReadOnlyList<Notification> GetByType(NotificationType type);
    int GetUnreadCount();
    bool MarkAsRead(int id);

    // ─── Chat ──────────────────────────────────────────────────────────────
    Chat AddChat(Chat c);
    Chat? GetChat(int id);
    IReadOnlyList<Chat> GetChats();

    // ─── Message ───────────────────────────────────────────────────────────
    Message AddMessage(Message m);
    IReadOnlyList<Message> GetMessagesByChat(int chatId);

    // ─── MediaItem ─────────────────────────────────────────────────────────
    MediaItem AddMedia(MediaItem item);
    MediaItem? GetMedia(int id);
    IReadOnlyList<MediaItem> GetGallery();
}