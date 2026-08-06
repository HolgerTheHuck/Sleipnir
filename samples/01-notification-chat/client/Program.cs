using System.Net;
using System.Net.Http;
using System.Text.Json;
using TrameClient.Trame;
using TrameCommon.Models;

const string BaseUrl = "https://localhost:5002";

// Trust the ASP.NET Core dev certificate for this demo only.
var httpHandler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
};
var httpClient = new HttpClient(httpHandler);

var rest = new TrameRestJsonClient(BaseUrl, httpClient);

Console.WriteLine("=== NotificationChat C# Client ===");
Console.WriteLine($"Base URL: {BaseUrl}\n");

// 1. Single REST call: load inbox
Console.WriteLine("1. Single REST call: Notification.GetInbox");
try
{
    var inbox = await rest.Call<List<NotificationDto>>(
        TrameCall.Init("Notification", "GetInbox").ToRequest()
    );
    Console.WriteLine($"   Inbox has {inbox?.Count ?? 0} items.");
    inbox?.Take(3).ToList().ForEach(n => Console.WriteLine($"   - [{n.Type}] {n.Title}: {n.Body}"));
}
catch (Exception ex)
{
    Console.WriteLine($"   ERROR: {ex.Message}");
}

// 2. Parallel batch: unread count + chats + gallery
Console.WriteLine("\n2. Parallel batch: UnreadCount + Chats + Gallery");
try
{
    var batch = new TrameMultiRequest
    {
        Mode = ExecutionMode.Parallel,
        Requests =
        [
            TrameCall.Init("Notification", "GetUnreadCount").ToRequest(),
            TrameCall.Init("Chat", "GetChats").ToRequest(),
            TrameCall.Init("Media", "GetGallery").ToRequest()
        ]
    };

    var responses = await rest.Call(batch);
    foreach (var r in responses ?? Enumerable.Empty<TrameResponse?>())
    {
        if (r is null) continue;
        Console.WriteLine($"   {r.Id}: code={r.Code}, data kind={r.Data?.ValueKind ?? JsonValueKind.Undefined}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   ERROR: {ex.Message}");
}

// 3. Dependency chain: CreateChat -> SendMessage -> GetMessages
Console.WriteLine("\n3. Dependency chain: CreateChat -> SendMessage -> GetMessages");
try
{
    var chain = new TrameMultiRequest
    {
        Mode = ExecutionMode.Serial,
        Requests =
        [
            TrameCall.Init("Chat", "CreateChat")
                .Param("name", "Demo Chat")
                .Param("participants", new List<string> { "Client", "Server" })
                .Named("create")
                .Exposes("$.id", "chatId")
                .ToRequest(),

            TrameCall.Init("Chat", "SendMessage")
                .WithAlias("@chatId")
                .Param("sender", "Client")
                .Param("text", "Hello from the C# client via Trame!")
                .Named("send")
                .ToRequest(),

            TrameCall.Init("Chat", "GetMessages")
                .WithAlias("@chatId")
                .Named("messages")
                .ToRequest()
        ]
    };

    var chainResponses = await rest.Call(chain);
    foreach (var r in chainResponses ?? Enumerable.Empty<TrameResponse?>())
    {
        if (r is null) continue;
        Console.WriteLine($"   {r.Id}: code={r.Code}");
    }

    var last = chainResponses?.FirstOrDefault(r => r?.Id == "messages");
    if (last?.Data is not null)
    {
        var messages = last.Data.Value.Deserialize<List<MessageDto>>();
        Console.WriteLine($"   Chat has {messages?.Count ?? 0} message(s).");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   ERROR: {ex.Message}");
}

// 4. WebSocket call
Console.WriteLine("\n4. WebSocket call: Notification.SendMail + Notification.GetById");
try
{
    var ws = new TrameWebSocketClient(BaseUrl);
    await ws.ConnectAsync();
    try
    {
        var sent = await ws.Call<NotificationDto>(
            TrameCall.Init("Notification", "SendMail")
                .Param("to", "client@trame.test")
                .Param("subject", "WebSocket test")
                .Param("body", "This mail arrived over the persistent WebSocket channel.")
                .ToRequest()
        );

        Console.WriteLine($"   Sent: [{sent?.Type}] {sent?.Title} (Id={sent?.Id})");

        var loaded = await ws.Call<NotificationDto>(
            TrameCall.Init("Notification", "GetById").Param("id", sent!.Id).ToRequest()
        );
        Console.WriteLine($"   Loaded: [{loaded?.Type}] {loaded?.Title}");
    }
    finally
    {
        await ws.DisposeAsync();
        await Task.Delay(500);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"   ERROR: {ex.Message}");
}

Console.WriteLine("\n=== C# Client Demo finished ===");

public class NotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime Timestamp { get; set; }
}

public class MessageDto
{
    public int Id { get; set; }
    public int ChatId { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
