using Microsoft.AspNetCore.Diagnostics;
using OperacionRescate.Api.Exceptions;

namespace OperacionRescate.Api.ErrorHandling;

/// <summary>
/// Convierte cualquier excepción en una respuesta segura. Decide el código HTTP y registra
/// solo lo inesperado (los errores de negocio 400/404/409 no son errores internos).
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public const string DetalleGenerico =
        "Ocurrió un error inesperado. Indique el correlationId al equipo de soporte.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AppException app)
        {
            await ProblemResponse.WriteAsync(httpContext, app.StatusCode, app.Title, app.Message);
        }
        else
        {
            logger.LogError(exception, "Excepción no controlada en {RequestPath}", httpContext.Request.Path.Value);
            await ProblemResponse.WriteAsync(
                httpContext, StatusCodes.Status500InternalServerError, "Error interno del servidor", DetalleGenerico);
        }

        return true;
    }
}
