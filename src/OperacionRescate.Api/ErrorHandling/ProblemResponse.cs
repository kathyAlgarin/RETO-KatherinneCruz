using Microsoft.AspNetCore.Mvc;

namespace OperacionRescate.Api.ErrorHandling;

/// <summary>Claves de HttpContext.Items compartidas entre el middleware y el manejador de errores.</summary>
public static class DiagnosticItems
{
    public const string CorrelationId = "Diagnostic.CorrelationId";

    public const string Exception = "Diagnostic.Exception";
}

/// <summary>Único lugar que da forma a las respuestas de error (application/problem+json, §5.5).</summary>
public static class ProblemResponse
{
    public const string ContentType = "application/problem+json";

    public static string ObtenerCorrelationId(HttpContext context)
        => context.Items.TryGetValue(DiagnosticItems.CorrelationId, out var id) && id is string s
            ? s
            : context.TraceIdentifier;

    public static Task WriteAsync(HttpContext context, int status, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Type = TipoPara(status),
            Title = title,
            Status = status,
            Detail = detail,
        };
        problem.Extensions["correlationId"] = ObtenerCorrelationId(context);

        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: ContentType);
    }

    private static string TipoPara(int status) => status switch
    {
        400 => "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1",
        404 => "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.5",
        409 => "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.10",
        >= 500 => "https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1",
        _ => "https://www.rfc-editor.org/rfc/rfc9110#section-15.5",
    };
}
