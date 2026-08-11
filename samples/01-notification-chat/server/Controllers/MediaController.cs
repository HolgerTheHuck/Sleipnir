using SleipnirCore.Attributes;
using Sleipnir.Samples.NotificationChat.Server.Data;
using Sleipnir.Samples.NotificationChat.Server.Models;

namespace Sleipnir.Samples.NotificationChat.Server.Controllers;

[SleipnirController("Media")]
public class MediaController(INotificationStore store)
{
    [SleipnirMethod("GetGallery")]
    public IReadOnlyList<MediaItem> GetGallery()
        => store.GetGallery();

    [SleipnirMethod("GetById")]
    public MediaItem? GetById(int id)
        => store.GetMedia(id);

    [SleipnirMethod("UploadImage")]
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

    [SleipnirMethod("UploadVideo")]
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
