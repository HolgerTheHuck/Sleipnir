using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

builder.Services.AddSleipnir(o =>
{
    o.UseSignalR = true;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.WebHost.UseUrls("https://localhost:5001");

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.UseSleipnirTransports();
app.MapSleipnir();

app.MapGet("/", () => Results.Redirect("/Sleipnir"));

app.Run();
