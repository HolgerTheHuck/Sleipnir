using TrameCore.Attributes;
using TrameCore.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Reflection;

namespace TrameHub.Extensions;

/// <summary>
/// Builder for registering Trame controllers with minimal boilerplate.
/// Supports convention-based, explicit, and lambda-based registration.
/// </summary>
public class TrameControllerBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Action<ITrameCore>> _registrations = new();

    internal TrameControllerBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers all types with [TrameController] from the specified assemblies.
    /// Controllers are added to <see cref="IServiceCollection"/> (scoped) immediately
    /// and registered with the invoker at <c>UseTrame</c> time.
    /// </summary>
    public TrameControllerBuilder FromAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Length > 0 ? assemblies : AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in TypeScanning.SafeGetTypes(assembly))
            {
                var attr = type.GetCustomAttributes(typeof(TrameControllerAttribute), true)
                    .OfType<TrameControllerAttribute>().FirstOrDefault();
                // AutoDiscover=false controllers are also excluded from the bulk FromAssemblies scan;
                // they can only be registered explicitly via Add<T>() / Register<T>().
                if (attr != null && attr.AutoDiscover)
                {
                    _services.AddScoped(type);
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
        _services.AddScoped(typeof(T));
        _registrations.Add(core => core.Register<T>());
        return this;
    }

    /// <summary>
    /// Registers a controller type with a specific service lifetime.
    /// </summary>
    public TrameControllerBuilder Add<T>(ServiceLifetime lifetime) where T : class
    {
        _services.Add(new ServiceDescriptor(typeof(T), typeof(T), lifetime));
        _registrations.Add(core => core.Register<T>());
        return this;
    }

    /// <summary>
    /// Registers a controller as a singleton (e.g. stateless service).
    /// </summary>
    public TrameControllerBuilder AddSingleton<T>() where T : class
    {
        _services.AddSingleton(typeof(T));
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
        // Service registrations were already added to IServiceCollection at builder-call
        // time (each Add<T>/FromAssemblies writes to _services immediately, R2). Here we
        // only register the controllers with the invoker core.
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
        // Route through the canonical AddTrame (single registration implementation, R1) so the
        // fluent overload is behaviorally identical: ConfigureHttpJsonOptions (camelCase wire +
        // TrameResponseJsonConverter), the SignalR MaximumParallelInvocationsPerClient>0 guard,
        // the MessagePack JsonElementResolver, all north-bound pass-throughs, the built-in
        // interceptor set (Auth/Telemetry/Logging), the TrameOptions DI singleton, and the rate
        // limiter. The fluent contract is *explicit* registration, so disable the bulk auto-scan
        // (the canonical path would otherwise AddScoped + invoker-register every [TrameController]
        // in the AppDomain; registration is idempotent for the same type, but the intent here is
        // opt-in only).
        options.AutoDiscoverControllers = false;
        services.AddTrame(options);

        var builder = new TrameControllerBuilder(services);
        configureControllers(builder);

        // We'll apply registrations in UseTrame (when the core is available)
        services.AddSingleton(builder);

        return services;
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