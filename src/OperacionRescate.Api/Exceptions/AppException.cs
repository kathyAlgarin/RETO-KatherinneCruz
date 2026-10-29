namespace OperacionRescate.Api.Exceptions;

/// <summary>
/// Error esperado de la aplicación (negocio, validación, no encontrado, conflicto).
/// Lleva el código HTTP y un mensaje seguro para el cliente; lo inesperado NO hereda de aquí.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(int statusCode, string title, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
        Title = title;
    }

    public int StatusCode { get; }

    public string Title { get; }
}

public sealed class ValidacionException(string detail)
    : AppException(StatusCodes.Status400BadRequest, "Solicitud inválida", detail);

public sealed class NoEncontradoException(string detail)
    : AppException(StatusCodes.Status404NotFound, "Recurso no encontrado", detail);

public sealed class ConflictoException(string detail, string title = "Conflicto")
    : AppException(StatusCodes.Status409Conflict, title, detail);
