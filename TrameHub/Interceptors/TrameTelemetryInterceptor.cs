using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TrameCommon.Results;
using TrameCore.Services;
using TrameCore.Tracing;

namespace TrameHub.Interceptors;

/// <summary>
/// Built-in Telemetry-Interceptor (Phase 1). Konsolidiert die Tracing- und Metrics-
/// Belange, die in v1.0 an acht Stellen fest im <c>TrameInvoker</c> verdrahtet waren,
/// in einem Interceptor. Nutzt den <see cref="TrameInvocationContext.Activity"/> (der
/// vom Invoker in <c>InvokeDi(single)</c> geöffnet und in den Context gelegt wird),
/// statt einen eigenen Span aufzumachen — kein Double-Count.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tracing</b>: setzt den OTel-Status (<see cref="TrameTracing.SetCallStatus"/>) und
/// recorded Exceptions (<see cref="TrameTracing.RecordException"/>) am Context-Activity.
/// Die Tags (<c>rpc.system</c>, <c>rpc.service</c>, <c>rpc.method</c>, <c>trame.request_id</c>,
/// <c>trame.binary.length</c>) werden vom Invoker beim <c>StartCall</c> gesetzt und bleiben.
/// </para>
/// <para>
/// <b>Metrics</b>: recordet <c>trame.call.duration</c> (Histogram), <c>trame.call.count</c>
/// und <c>trame.error.count</c> (Counter) via <see cref="TrameMetrics"/>. Kostenneutral
/// ohne MetricReader. Tags: <c>rpc.system/service/method</c>, <c>trame.error_category</c>,
/// <c>trame.success</c>.
/// </para>
/// <para>
/// <b>Logging</b>: strukturierte Logs mit OTel-RPC-Semantic-Conventions-Feldern
/// (<c>rpc.system</c>, <c>rpc.service</c>, <c>rpc.method</c>, <c>trame.request_id</c>,
/// <c>trame.duration_ms</c>, <c>trame.status_code</c>, <c>trame.error_category</c>) —
/// ergänzt den bestehenden <c>TrameLoggingInterceptor</c> (der Duration misst und
/// Trace/Debug/Error loggt) um die konventions-konformen Feldnamen. Beide können
/// koexistieren; dieser Interceptor trägt die OTel-Konventionen, der Logging-Interceptor
/// bleibt als einfacher Dauer-Logger erhalten (abwärtskompatibel).
/// </para>
/// <para>
/// Reihenfolge: läuft *nach* Auth (außen) und *vor* der Method-Invocation (innen) —
/// Tracing/Metrics messen nur autorisierten Traffic, wie in
/// <c>docs/design/phase-1-interceptor-pipeline.md</c> festgelegt.
/// </para>
/// </remarks>
public class TrameTelemetryInterceptor : ITrameInterceptor
{
    private readonly ILogger<TrameTelemetryInterceptor> _logger;

    public TrameTelemetryInterceptor(ILogger<TrameTelemetryInterceptor> logger)
    {
        _logger = logger;
    }

    public async Task<TrameResponse?> InvokeAsync(
        TrameInvocationContext context,
        TrameInvocationDelegate next)
    {
        var stopwatch = Stopwatch.StartNew();
        var activity = context.Activity;

        TrameResponse? response;
        try
        {
            response = await next(context);
            stopwatch.Stop();

            // Tracing: OTel-Status am bestehenden Span (kein neuer Span — Double-Count).
            TrameTracing.SetCallStatus(activity, response);

            // Metrics + Logging für den Erfolgs-/Business-Fehler-Pfad.
            RecordTelemetry(context, response, stopwatch.Elapsed.TotalMilliseconds, category: response?.Error?.Category ?? TrameErrorCategory.None);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Tracing: Exception auf dem Span recorden (exception.type/message/stacktrace).
            TrameTracing.RecordException(activity, ex);

            // Metrics + Logging für den Exception-Pfad (5xx, Internal). Die Category
            // ist hier Internal — der Invoker übersetzt die Exception später in ein
            // generisches 500. Re-throw, damit der Invoker die Response erzeugt.
            RecordTelemetry(context, response: null, stopwatch.Elapsed.TotalMilliseconds,
                category: TrameErrorCategory.Internal, exception: ex);

            throw;
        }
    }

    private void RecordTelemetry(
        TrameInvocationContext context,
        TrameResponse? response,
        double durationMs,
        TrameErrorCategory category,
        Exception? exception = null)
    {
        // Metrics (kostenneutral ohne Reader).
        TrameMetrics.RecordCall(context.Request, response, durationMs, category);

        // Strukturiertes Logging mit OTel-RPC-Semantic-Conventions-Feldern.
        var success = response?.IsSuccess == true;
        var statusCode = response?.Code ?? 0;
        var logLevel = success ? LogLevel.Debug :
                       exception != null ? LogLevel.Error : LogLevel.Warning;

        if (exception != null)
        {
            _logger.LogError(exception,
                "RPC {RpcSystem}.{RpcService}.{RpcMethod} failed [{TrameStatusCode}] {TrameErrorCategory} after {TrameDurationMs}ms [{TrameRequestId}]",
                "trame", context.ControllerName, context.MethodName,
                statusCode, category, durationMs, context.RequestId);
        }
        else
        {
            _logger.Log(logLevel,
                "RPC {RpcSystem}.{RpcService}.{RpcMethod} {TrameSuccess} [{TrameStatusCode}] {TrameErrorCategory} in {TrameDurationMs}ms [{TrameRequestId}]",
                "trame", context.ControllerName, context.MethodName,
                success, statusCode, category, durationMs, context.RequestId);
        }
    }
}