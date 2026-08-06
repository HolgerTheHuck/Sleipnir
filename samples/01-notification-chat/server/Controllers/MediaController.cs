using TrameCore.Attributes;
using Trame.Samples.NotificationChat.Server.Data;
using Trame.Samples.NotificationChat.Server.Models;

namespace Trame.Samples.NotificationChat.Server.Controllers;

[TrameController("Media")]
public class MediaController(NotificationStore store)
{
    [TrameMethod("GetGallery")]
    public IReadOnlyList<MediaItem> GetGallery()
        => store.GetGallery();

    [TrameMethod("GetById")]
    public MediaItem? GetById(int id)
        => store.GetMedia(id);

    [TrameMethod("UploadImage")]
    public MediaItem UploadImage(string url, string mimeType, long sizeBytes, string? thumbnailUrl = null)
    {
        var item = new MediaItem
        {
            Url = url,
            MimeType = mimeType,
            SizeBytes = sizeBytes,
            MediaType = MediaType.Image,
            ThumbnailUrl = thumbnailUrl
        };
        return store.AddMedia(item);
    }

    [TrameMethod("UploadVideo")]
    public MediaItem UploadVideo(string url, string mimeType, long sizeBytes, string? thumbnailUrl = null)
    {
        var item = new MediaItem
        {
            Url = url,
            MimeType = mimeType,
            SizeBytes = sizeBytes,
            MediaType = MediaType.Video,
            ThumbnailUrl = thumbnailUrl
        };
        return store.AddMedia(item);
    }
}
