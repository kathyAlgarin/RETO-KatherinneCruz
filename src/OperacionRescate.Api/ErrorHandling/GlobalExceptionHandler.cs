using Microsoft.AspNetCore.Diagnostics;
using OperacionRescate.Api.Exceptions;

namespace OperacionRescate.Api.ErrorHandling;

/// <summary>
/// Convierte cualquier excepción en una respuesta segura. Decide el código HTTP; no registra:
/// deja la excepción en HttpContext.Items para que RequestDiagnosticMiddleware (que envuelve a
/// este manejador y conoce el estado final) la registre una sola vez.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public const string DetalleGenerico =
        "Ocurrió un error inesperado. Indique el correlationId al equipo de soporte.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        httpContext.Items[DiagnosticItems.Exception] = exception;

        if (exception is AppException app)
        {
            await ProblemResponse.WriteAsync(httpContext, app.StatusCode, app.Title, app.Message);
        }
        else
        {
            await ProblemResponse.WriteAsync(
                httpContext, StatusCodes.Status500InternalServerError, "Error interno del servidor", DetalleGenerico);
        }

        return true;
    }
}
