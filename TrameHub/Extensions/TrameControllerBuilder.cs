using TrameCore.Attributes;
using TrameCore.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Reflection;
using System.Threading.RateLimiting;

namespace TrameHub.Extensions;

/// <summary>
/// Builder for registering Trame controllers with minimal boilerplate.
/// Supports convention-based, explicit, and lambda-based registration.
/// </summary>
public class TrameControllerBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Action<ITrameCore>> _registrations = new();
    private readonly List<(Type type, ServiceLifetime lifetime)> _serviceRegistrations = new();

    internal TrameControllerBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers all types with [TrameController] from the specified assemblies.
    /// </summary>
    public TrameControllerBuilder FromAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Length > 0 ? assemblies : AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in TypeScanning.SafeGetTypes(assembly))
            {
                var attr = type.GetCustomAttributes(typeof(TrameControllerAttribute), true)
                    .OfType<TrameControllerAttribute>().FirstOrDefault();
                // AutoDiscover=false-Controller werden auch vom Bulk-FromAssemblies-Skan ausgeschlossen;
                // sie sind nur über Add<T>() / Register<T>() explizit zu registrieren.
                if (attr != null && attr.AutoDiscover)
                {
                    _serviceRegistrations.Add((type, ServiceLifetime.Scoped));
                    _registrations.Add(core => core.Register(type));
                }
            }
        }
        return this;
    }

    /// <summary>
    /// Explicitly registers a controller type.
    /// The type must have [TrameController] attribute.
    /// </summary>
    public TrameControllerBuilder Add<T>() where T : class
    {
        _serviceRegistrations.Add((typeof(T), ServiceLifetime.Scoped));
        _registrations.Add(core => core.Register<T>());
        return this;
    }

    /// <summary>
    /// Registers a controller type with a specific service lifetime.
    /// </summary>
    public TrameControllerBuilder Add<T>(ServiceLifetime lifetime) where T : class
    {
        _serviceRegistrations.Add((typeof(T), lifetime));
        _registrations.Add(core => core.Register<T>());
        return this;
    }

    /// <summary>
    /// Registers a controller as a singleton (e.g. stateless service).
    /// </summary>
    public TrameControllerBuilder AddSingleton<T>() where T : class
    {
        _serviceRegistrations.Add((typeof(T), ServiceLifetime.Singleton));
        _registrations.Add(core => core.Register<T>());
        return this;
    }

    /// <summary>
    /// Registers a controller with a factory method.
    /// The type must have [TrameController] attribute.
    /// </summary>
    public TrameControllerBuilder Add<T>(Func<IServiceProvider, T> factory) where T : class
    {
        _services.AddScoped(factory);
        _registrations.Add(core => core.Register<T>());
        return this;
    }

    internal void Apply(IServiceProvider serviceProvider, ITrameCore core)
    {
        // Service registrations were already added to IServiceCollection in AddTrame()
        // Just register controllers with the core
        foreach (var registration in _registrations)
        {
            registration(core);
        }
    }
}

/// <summary>
/// Extension methods for registering Trame controllers with a fluent builder API.
/// </summary>
public static class TrameRegistrationExtensions
{
    /// <summary>
    /// Registers Trame controllers using a fluent builder.
    /// Reduces boilerplate compared to [TrameController] attribute scanning.
    ///
    /// Usage:
    /// <code>
    /// builder.Services.AddTrame(options, controllers => controllers
    ///     .Add&lt;CustomerHandler&gt;()
    ///     .AddSingleton&lt;ConfigHandler&gt;()
    ///     .FromAssemblies(typeof(Program).Assembly));
    /// </code>
    /// </summary>
    public static IServiceCollection AddTrame(
        this IServiceCollection services,
        TrameOptions options,
        Action<TrameControllerBuilder> configureControllers)
    {
        // Call the original AddTrame (without auto-scan)
        AddTrameCore(services, options);

        var builder = new TrameControllerBuilder(services);
        configureControllers(builder);

        // We'll apply registrations in UseTrame (when the core is available)
        services.AddSingleton(builder);

        return services;
    }

    private static void AddTrameCore(IServiceCollection services, TrameOptions options)
    {
        if (options.UseSignalR)
        {
            var fastHub = services.AddSignalR(o =>
            {
                o.EnableDetailedErrors = options.EnableDetailedErrors;
                o.MaximumReceiveMessageSize = options.MaximumReceiveMessageSize;
                o.StreamBufferCapacity = options.StreamBufferCapacity;
                o.MaximumParallelInvocationsPerClient = options.MaximumParallelInvocationsPerClient;
            });

            if (options.UseMessagePack)
                fastHub.AddMessagePackProtocol();
        }

        services.AddRateLimiter(rateLimiterOptions =>
        {
            if (options.RateLimitPermitLimit > 0)
            {
                rateLimiterOptions.AddFixedWindowLimiter("trame", opt =>
                {
                    opt.PermitLimit = options.RateLimitPermitLimit;
                    opt.Window = TimeSpan.FromSeconds(options.RateLimitWindowSeconds);
                    opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
                    opt.QueueLimit = 0;
                });
                rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            }
        });

        services.AddSingleton<ITrameCore>(sp =>
        {
            var invoker = new TrameCore.Services.TrameInvoker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<TrameCore.Services.TrameInvoker>>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TrameCore.Services.TrameInvoker>.Instance,
                sp.GetService<IEnumerable<TrameCore.Services.ITrameInterceptor>>());
            // Kardinalitäts-Caps durchreichen (Default 1000/10000, 0 = unbegrenzt).
            // Detailed Errors analog dem Main-Pfad über die Options/Environment.
            var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            invoker.EnableDetailedErrors = options.EnableDetailedErrors || (env?.IsDevelopment() ?? false);
            invoker.MaxParameterArrayLength = options.MaxParameterArrayLength;
            invoker.MaxResultElementCount = options.MaxResultElementCount;
            invoker.AliasBindingMode = options.AliasBindingMode;
            return invoker;
        });
        services.AddSingleton<TrameCore.Services.ITrameInterceptor, TrameCore.Services.TrameLoggingInterceptor>();
    }

    /// <summary>
    /// Registers Trame with auto-discovery from the calling assembly only (faster than scanning all assemblies).
    /// </summary>
    public static IServiceCollection AddTrameFromCurrentAssembly(
        this IServiceCollection services,
        TrameOptions options)
    {
        var assembly = Assembly.GetCallingAssembly();
        return services.AddTrame(options, controllers => controllers.FromAssemblies(assembly));
    }
}