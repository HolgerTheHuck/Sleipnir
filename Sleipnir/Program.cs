using Sleipnir.Grpc;
using Sleipnir.Api;
using Sleipnir.Services;
using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddGrpc();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins is { Length: > 0 })
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

builder.Services.AddSleipnir(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.UseMessagePack = true;          // nur relevant, wenn UseSignalR = true
    options.UseSignalR = true;              // optionaler 2. Kanal — WebSocket ist der Default
    options.MaximumParallelInvocationsPerClient = 100;
    options.MaximumReceiveMessageSize = 102400;
    options.StreamBufferCapacity = 100;
    options.RateLimitPermitLimit = builder.Environment.IsProduction() ? 50 : 0;
    options.RateLimitWindowSeconds = 10;
});

builder.Services.AddSingleton<CustomerService>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRateLimiter();

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseDefaultFiles();

app.UseRouting();
app.UseAuthorization();

// Sleipnir-Transport-Middleware (WebSocket primär) + Controller-Registrierung in einem Aufruf.
app.UseSleipnirTransports();

app.MapControllers();

app.MapRazorPages();

// Sleipnir-Endpunkte in einem Aufruf: REST + Developer-UI + SignalR-Hub (da UseSignalR = true).
app.MapSleipnir();

app.MapGrpcService<CustomerGrpcService>();

app.Run();
