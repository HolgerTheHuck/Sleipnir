using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

builder.Services.AddTrame(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.WebHost.UseUrls("https://localhost:5001");

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.UseTrameTransports();
app.MapTrame();

app.MapGet("/", () => Results.Redirect("/Trame"));

app.Run();
