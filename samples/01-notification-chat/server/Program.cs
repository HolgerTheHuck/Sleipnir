using Sleipnir.Samples.NotificationChat.Server.Data;
using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin =>
            new Uri(origin).IsLoopback &&
            (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
             origin.StartsWith("https://localhost:", StringComparison.OrdinalIgnoreCase)))
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddSingleton<INotificationStore, NotificationStore>();

builder.Services.AddSleipnir(o =>
{
    o.UseSignalR = true;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.WebHost.UseUrls("https://localhost:5002");

var app = builder.Build();

app.UseCors();
app.UseRouting();
app.UseStaticFiles();

app.UseSleipnirTransports();
app.MapSleipnir();

app.MapGet("/", () => Results.Redirect("/Sleipnir"));

app.Run();
