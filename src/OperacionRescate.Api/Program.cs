using OperacionRescate.Api.ErrorHandling;
using OperacionRescate.Api.Logging;
using OperacionRescate.Api.Middleware;
using OperacionRescate.Api.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog: niveles y consola vienen de la sección "Serilog" de appsettings; el archivo se declara
// aquí porque su carpeta se calcula (ver LogDirectory). FromLogContext lleva CorrelationId y
// LabRunId a cada evento. Rotación diaria conservando 14 archivos, formato JSON Lines.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .WriteTo.File(
        new JsonLinesFormatter(),
        Path.Combine(
            LogDirectory.Resolve(context.Configuration, context.HostingEnvironment.ContentRootPath),
            "operacion-rescate-.jsonl"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

builder.Services.AddControllers();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<IInventarioService, InventarioService>();
builder.Services.AddSingleton<IPedidoService, PedidoService>();

var app = builder.Build();

// Orden: el diagnóstico va por FUERA del manejador de excepciones. Así, cuando escribe el evento
// de finalización, el manejador ya generó la respuesta y StatusCode es el final.
app.UseMiddleware<RequestDiagnosticMiddleware>();

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
