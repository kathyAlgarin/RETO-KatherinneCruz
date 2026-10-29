using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using OperacionRescate.Api.ErrorHandling;
using OperacionRescate.Api.Exceptions;
using Serilog.Context;

namespace OperacionRescate.Api.Middleware;

/// <summary>
/// Middleware más externo del pipeline: correlación (R1), ejecución de laboratorio (R2), tiempo
/// (R3), registro de excepciones (R4, R7), un único evento de finalización (R5), contexto aislado
/// por petición (R6) y sin datos sensibles (R8).
/// </summary>
public sealed partial class RequestDiagnosticMiddleware
{
    public const string CorrelationHeader = "X-Correlation-ID";

    public const string RunIdHeader = "X-Lab-Run-Id";

    public const string SinRun = "sin-run";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestDiagnosticMiddleware> _logger;
    private readonly TimeProvider _time;

    public RequestDiagnosticMiddleware(
        RequestDelegate next, ILogger<RequestDiagnosticMiddleware> logger, TimeProvider time)
    {
        _next = next;
        _logger = logger;
        _time = time;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var inicio = _time.GetTimestamp();
        var correlationId = ResolverCorrelationId(context.Request.Headers[CorrelationHeader]);
        var labRunId = ResolverLabRunId(context.Request.Headers[RunIdHeader]);

        context.Items[DiagnosticItems.CorrelationId] = correlationId;

        // La cabecera se escribe justo antes de enviar la respuesta: el manejador global de
        // excepciones limpia las cabeceras al generar un error y OnStarting es el único punto
        // en que aún es seguro modificarlas (después de empezar a enviar ya no se puede).
        context.Response.OnStarting(static state =>
        {
            var (ctx, id) = ((HttpContext, string))state;
            ctx.Response.Headers[CorrelationHeader] = id;
            return Task.CompletedTask;
        }, (context, correlationId));

        // LogContext fluye con la petición (AsyncLocal): dos peticiones simultáneas no comparten datos.
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("LabRunId", labRunId))
        {
            Exception? escapada = null;
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                // Solo llega aquí lo que el manejador global no pudo resolver (R7). No se relanza:
                // un segundo registro (Kestrel) duplicaría la excepción.
                escapada = ex;
                await ResponderFalloAsync(context);
            }

            var excepcion = escapada ?? ExcepcionManejada(context);
            if (excepcion is not null and not AppException)
            {
                _logger.LogError(excepcion, "Excepción no controlada en {RequestPath}", context.Request.Path.Value);
            }

            RegistrarFinalizacion(context, inicio, hubeFalloInterno: escapada is not null);
        }
    }

    private void RegistrarFinalizacion(HttpContext context, long inicio, bool hubeFalloInterno)
    {
        var elapsedMs = _time.GetElapsedTime(inicio).TotalMilliseconds;
        var status = context.Response.StatusCode;
        var nivel = hubeFalloInterno || status >= 500 ? LogLevel.Error
            : status >= 400 ? LogLevel.Warning
            : LogLevel.Information;

        // ElapsedMs entra por un ámbito que envuelve solo esta llamada: lo lleva únicamente este evento.
        using (_logger.BeginScope(new Dictionary<string, object> { ["ElapsedMs"] = elapsedMs }))
        {
            _logger.Log(
                nivel,
                "HTTP {RequestMethod} {RequestPath} finalizó con {StatusCode}",
                context.Request.Method,
                context.Request.Path.Value,   // solo la ruta: nunca la query string (R8)
                status);
        }
    }

    private static Exception? ExcepcionManejada(HttpContext context)
        => context.Items.TryGetValue(DiagnosticItems.Exception, out var ex) ? ex as Exception : null;

    private static async Task ResponderFalloAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            // Ya se enviaron cabeceras/estado: no se puede reemplazar la respuesta. Se corta la
            // conexión para que el cliente no tome por completa una respuesta truncada.
            context.Abort();
            return;
        }

        context.Response.Clear();
        await ProblemResponse.WriteAsync(
            context, StatusCodes.Status500InternalServerError,
            "Error interno del servidor", GlobalExceptionHandler.DetalleGenerico);
    }

    /// <summary>Acepta solo un GUID 8-4-4-4-12; cualquier otra cosa se reemplaza por uno nuevo.</summary>
    internal static string ResolverCorrelationId(StringValues valores)
    {
        if (valores.Count == 1
            && valores[0] is { Length: 36 } texto
            && Guid.TryParseExact(texto, "D", out _))
        {
            return texto;
        }

        return Guid.NewGuid().ToString("D");
    }

    internal static string ResolverLabRunId(StringValues valores)
        => valores.Count == 1 && valores[0] is { } texto && RunIdRegex().IsMatch(texto)
            ? texto
            : SinRun;

    // \z (no $): con $ una cabecera terminada en salto de línea pasaría la validación.
    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex RunIdRegex();
}
