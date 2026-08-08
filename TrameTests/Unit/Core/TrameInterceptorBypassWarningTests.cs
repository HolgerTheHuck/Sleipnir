using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrameCommon.Models;
using TrameCore.Services;
using TrameHub.Extensions;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// R5: <see cref="UseTrame"/> logs a one-time warning when a user registered custom
/// interceptors, because they run on the single-call path only in 1.1.x and are silently
/// bypassed on batches. Authorization is unaffected (enforced structurally); the warning
/// keeps the bypass from staying silent. The real fix is tracked for 1.2 (ROADMAP.md R7).
/// </summary>
public class TrameInterceptorBypassWarningTests
{
    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public readonly List<(string Category, LogLevel Level, string Message)> Logs = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Logs);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, List<(string, LogLevel, string)> logs) : ILogger
    {
        private sealed class NullScope : IDisposable { public void Dispose() { } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NullScope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logs.Add((category, logLevel, formatter(state, exception)));
    }

    private sealed class DummyInterceptor : ITrameInterceptor
    {
        public Task<TrameResponse?> InvokeAsync(TrameInvocationContext context, TrameInvocationDelegate next)
            => next(context);
    }

    private static (CaptureLoggerProvider provider, ServiceProvider sp) BuildHost(TrameOptions options)
    {
        var provider = new CaptureLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider));
        services.AddTrame(options);
        var sp = services.BuildServiceProvider();
        return (provider, sp);
    }

    [Fact]
    public void UseTrame_WithUserInterceptors_LogsBatchBypassWarning()
    {
        var (provider, sp) = BuildHost(new TrameOptions
        {
            RegisterBuiltInInterceptors = false, // minimal: no built-in auth/telemetry needed here
            Interceptors = { new DummyInterceptor() },
        });
        using (sp)
        {
            new ApplicationBuilder(sp).UseTrame();
        }

        provider.Logs.Should().Contain(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("single-call path only", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("Interceptors=1"),
            "a user interceptor must trigger the one-time batch-bypass warning");
    }

    [Fact]
    public void UseTrame_WithoutUserInterceptors_DoesNotLogBatchBypassWarning()
    {
        var (provider, sp) = BuildHost(new TrameOptions { RegisterBuiltInInterceptors = false });
        using (sp)
        {
            new ApplicationBuilder(sp).UseTrame();
        }

        provider.Logs.Should().NotContain(l => l.Message.Contains("single-call path only", StringComparison.OrdinalIgnoreCase),
            "no user interceptors → no batch-bypass warning");
    }
}