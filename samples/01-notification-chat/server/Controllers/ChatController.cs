using TrameCore.Attributes;
using Trame.Samples.NotificationChat.Server.Data;
using Trame.Samples.NotificationChat.Server.Models;

namespace Trame.Samples.NotificationChat.Server.Controllers;

[TrameController("Chat")]
public class ChatController(INotificationStore store)
{
    [TrameMethod("GetChats")]
    public IReadOnlyList<Chat> GetChats()
        => store.GetChats();

    [TrameMethod("GetChat")]
    public Chat? GetChat(int id)
        => store.GetChat(id);

    [TrameMethod("GetMessages")]
    public IReadOnlyList<Message> GetMessages(int chatId)
        => store.GetMessagesByChat(chatId);

    [TrameMethod("CreateChat")]
    public Chat CreateChat(string name, List<string> participants)
    {
        var chat = new Chat
        {
            Name = name,
            Participants = participants ?? new List<string>()
        };
        return store.AddChat(chat);
    }

    [TrameMethod("SendMessage")]
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

    [TrameMethod("SendMessageWithAttachment")]
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
