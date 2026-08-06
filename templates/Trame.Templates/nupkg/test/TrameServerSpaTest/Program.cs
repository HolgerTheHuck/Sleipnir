using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddTrame(o =>
{
    o.UseSignalR = true;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.WebHost.UseUrls("https://localhost:5001");

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();
app.UseRouting();

app.UseTrameTransports();
app.MapTrame();

app.MapGet("/", () => Results.Redirect("/Trame"));

app.Run();
