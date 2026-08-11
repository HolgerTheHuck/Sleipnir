using SleipnirCore.Attributes;
using Sleipnir.Samples.NotificationChat.Server.Data;
using Sleipnir.Samples.NotificationChat.Server.Models;

namespace Sleipnir.Samples.NotificationChat.Server.Controllers;

[SleipnirController("Chat")]
public class ChatController(INotificationStore store)
{
    [SleipnirMethod("GetChats")]
    public IReadOnlyList<Chat> GetChats()
        => store.GetChats();

    [SleipnirMethod("GetChat")]
    public Chat? GetChat(int id)
        => store.GetChat(id);

    [SleipnirMethod("GetMessages")]
    public IReadOnlyList<Message> GetMessages(int chatId)
        => store.GetMessagesByChat(chatId);

    [SleipnirMethod("CreateChat")]
    public Chat CreateChat(string name, List<string> participants)
    {
        var chat = new Chat
        {
            Name = name,
            Participants = participants ?? new List<string>()
        };
        return store.AddChat(chat);
    }

    [SleipnirMethod("SendMessage")]
    public Message SendMessage(int chatId, string sender, string text)
    {
        var message = new Message
        {
            ChatId = chatId,
            Sender = sender,
            Text = text
        };
        return store.AddMessage(message);
    }

    [SleipnirMethod("SendMessageWithAttachment")]
    public Message SendMessageWithAttachment(int chatId, string sender, string text, int mediaId)
    {
        var media = store.GetMedia(mediaId);
        var message = new Message
        {
            ChatId = chatId,
            Sender = sender,
            Text = text
        };

        if (media is not null)
        {
            message.Attachments.Add(new MediaAttachment
            {
                Id = media.Id,
                Url = media.Url,
                MimeType = media.MimeType,
                SizeBytes = media.SizeBytes,
                ThumbnailUrl = media.ThumbnailUrl,
                MediaType = media.MediaType
            });
        }

        return store.AddMessage(message);
    }
}
