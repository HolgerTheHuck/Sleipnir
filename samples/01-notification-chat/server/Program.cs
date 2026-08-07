using Trame.Samples.NotificationChat.Server.Data;
using TrameHub.Extensions;
using TrameServer;

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

builder.Services.AddTrame(o =>
{
    o.UseSignalR = true;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.WebHost.UseUrls("https://localhost:5002");

var app = builder.Build();

app.UseCors();
app.UseRouting();
app.UseStaticFiles();

app.UseTrameTransports();
app.MapTrame();

app.MapGet("/", () => Results.Redirect("/Trame"));

app.Run();
