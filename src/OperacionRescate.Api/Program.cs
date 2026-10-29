using OperacionRescate.Api.ErrorHandling;
using OperacionRescate.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddSingleton<IInventarioService, InventarioService>();
builder.Services.AddSingleton<IPedidoService, PedidoService>();

var app = builder.Build();

app.UseExceptionHandler(_ => { });

// Respuestas sin cuerpo (ruta desconocida, 415, etc.) también salen como problem+json.
app.UseStatusCodePages(context =>
{
    var status = context.HttpContext.Response.StatusCode;
    return ProblemResponse.WriteAsync(
        context.HttpContext, status,
        status >= 500 ? "Error interno del servidor" : "Solicitud no válida",
        status >= 500 ? GlobalExceptionHandler.DetalleGenerico : "No se pudo procesar la solicitud.");
});

app.MapControllers();

app.Run();

public partial class Program { }
