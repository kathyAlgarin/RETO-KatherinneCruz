using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OperacionRescate.Api.ErrorHandling;
using OperacionRescate.Api.Middleware;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OperacionRescate.Tests.Support;

/// <summary>Reloj manual: 1 tick = 1 ms. Las pruebas controlan el tiempo, no dependen del reloj real.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void AdvanceMs(long ms) => Interlocked.Add(ref _ticks, ms);
}

/// <summary>Sumidero en memoria con espera por evento (la finalización se escribe tras responder).</summary>
public sealed class CollectorSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];
    private readonly object _lock = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_lock)
        {
            _events.Add(logEvent);
        }
    }

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<LogEvent> Completions => [.. Events.Where(EsFinalizacion)];

    public static bool EsFinalizacion(LogEvent e) => e.Properties.ContainsKey("ElapsedMs");

    /// <summary>Espera (máx. 5 s) a que existan <paramref name="cantidad"/> eventos de finalización.</summary>
    public async Task EsperarFinalizacionesAsync(int cantidad = 1)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (Completions.Count < cantidad)
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException($"Se esperaban {cantidad} eventos de finalización y hay {Completions.Count}.");
            }

            await Task.Delay(10);
        }
    }

    public static string Texto(LogEvent e, string propiedad)
        => ((ScalarValue)e.Properties[propiedad]).Value?.ToString() ?? "";
}

/// <summary>Pipeline mínimo real: RequestDiagnosticMiddleware -> manejador global -> endpoint de prueba.</summary>
public sealed class DiagnosticHost : IAsyncDisposable
{
    private readonly IHost _host;

    private DiagnosticHost(IHost host, HttpClient client, CollectorSink sink, ManualTimeProvider time)
    {
        _host = host;
        Client = client;
        Sink = sink;
        Time = time;
    }

    public HttpClient Client { get; }

    public CollectorSink Sink { get; }

    public ManualTimeProvider Time { get; }

    public int Invocaciones;

    public static async Task<DiagnosticHost> CrearAsync(Func<HttpContext, ManualTimeProvider, Task> endpoint)
    {
        var sink = new CollectorSink();
        var time = new ManualTimeProvider();
        // Niveles reales de la API (appsettings.json): así se prueba también esa configuración.
        var serilog = new LoggerConfiguration()
            .ReadFrom.Configuration(NivelesDeLaApi())
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        DiagnosticHost? resultado = null;
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging(b => b.ClearProviders().AddSerilog(serilog, dispose: false));
                    services.AddSingleton<TimeProvider>(time);
                    services.AddExceptionHandler<GlobalExceptionHandler>();
                })
                .Configure(app =>
                {
                    app.UseMiddleware<RequestDiagnosticMiddleware>();
                    app.UseExceptionHandler(_ => { });
                    app.Run(context =>
                    {
                        Interlocked.Increment(ref resultado!.Invocaciones);
                        return endpoint(context, time);
                    });
                }))
            .StartAsync();

        resultado = new DiagnosticHost(host, host.GetTestClient(), sink, time);
        return resultado;
    }

    private static IConfiguration NivelesDeLaApi()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !directorio.EnumerateFiles("*.sln").Any())
        {
            directorio = directorio.Parent;
        }

        var ruta = Path.Combine(directorio!.FullName, "src", "OperacionRescate.Api", "appsettings.json");
        var completa = new ConfigurationBuilder().AddJsonFile(ruta).Build();
        var soloNiveles = completa.GetSection("Serilog:MinimumLevel").AsEnumerable(false)
            .Where(par => par.Value is not null);
        return new ConfigurationBuilder().AddInMemoryCollection(soloNiveles).Build();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}

public static class JsonHelper
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
