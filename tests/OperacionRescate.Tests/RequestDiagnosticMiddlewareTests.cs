using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OperacionRescate.Api.Exceptions;
using OperacionRescate.Tests.Support;
using Serilog.Events;

namespace OperacionRescate.Tests;

/// <summary>Contrato R1–R8 del RequestDiagnosticMiddleware (INC-005, INC-008 y manejo de excepciones).</summary>
public class RequestDiagnosticMiddlewareTests
{
    private const string Cabecera = "X-Correlation-ID";

    private static Task<DiagnosticHost> Crear(Func<HttpContext, ManualTimeProvider, Task>? endpoint = null)
        => DiagnosticHost.CrearAsync(endpoint ?? ((ctx, _) => ctx.Response.WriteAsync("ok")));

    private static string? Cid(HttpResponseMessage r)
        => r.Headers.TryGetValues(Cabecera, out var v) ? v.SingleOrDefault() : null;

    // ---- R1 / INC-005: correlación ----------------------------------------------------------

    [Fact]
    public async Task INC005_AceptaYDevuelveUnCorrelationIdValido()
    {
        await using var host = await Crear();
        var enviado = Guid.NewGuid().ToString();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.Add(Cabecera, enviado);
        var respuesta = await host.Client.SendAsync(request);
        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal(enviado, Cid(respuesta));
        Assert.Equal(enviado, CollectorSink.Texto(host.Sink.Completions.Single(), "CorrelationId"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es un guid; DROP TABLE")]
    [InlineData("{8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f11}")]
    [InlineData("8d2c5e2a3a0e4e3a9d7a0d0b3e3c9f11")]
    [InlineData("abc-8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f11-zzz")]
    [InlineData("8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f1g")]
    public async Task INC005_ReemplazaCorrelationIdMalformadoPorUnGuidNuevo(string hostil)
    {
        await using var host = await Crear();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.TryAddWithoutValidation(Cabecera, hostil);
        var respuesta = await host.Client.SendAsync(request);

        var devuelto = Cid(respuesta);
        Assert.NotEqual(hostil, devuelto);
        Assert.True(Guid.TryParseExact(devuelto, "D", out _), $"no es un GUID 8-4-4-4-12: '{devuelto}'");
    }

    [Fact]
    public async Task INC005_ReemplazaCorrelationIdDemasiadoLargo()
    {
        await using var host = await Crear();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.TryAddWithoutValidation(Cabecera, new string('x', 300));
        var respuesta = await host.Client.SendAsync(request);

        Assert.True(Guid.TryParseExact(Cid(respuesta), "D", out _));
    }

    [Fact]
    public async Task INC005_SinCabecera_GeneraUnIdentificadorDistintoPorPeticion()
    {
        await using var host = await Crear();

        var a = Cid(await host.Client.GetAsync("/x"));
        var b = Cid(await host.Client.GetAsync("/x"));

        Assert.True(Guid.TryParseExact(a, "D", out _));
        Assert.True(Guid.TryParseExact(b, "D", out _));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task INC005_PeticionesSimultaneas_NoComparten_NiHeredan_Contexto()
    {
        // Cada petición espera a que las 20 estén dentro del pipeline antes de registrar su evento.
        var dentro = new CountdownEvent(20);
        await using var host = await Crear(async (ctx, _) =>
        {
            dentro.Signal();
            await Task.Run(() => dentro.Wait(TimeSpan.FromSeconds(5)));
            await ctx.Response.WriteAsync("ok");
        });

        var enviados = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid().ToString()).ToList();
        var respuestas = await Task.WhenAll(enviados.Select(async (id, i) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/x{i}");
            request.Headers.Add(Cabecera, id);
            request.Headers.Add("X-Lab-Run-Id", $"run-{i}");
            return await host.Client.SendAsync(request);
        }));
        await host.Sink.EsperarFinalizacionesAsync(20);

        Assert.Equal(enviados, respuestas.Select(Cid));
        foreach (var evento in host.Sink.Completions)
        {
            var i = int.Parse(CollectorSink.Texto(evento, "RequestPath")[2..]);
            Assert.Equal(enviados[i], CollectorSink.Texto(evento, "CorrelationId"));
            Assert.Equal($"run-{i}", CollectorSink.Texto(evento, "LabRunId"));
        }
    }

    [Fact]
    public async Task INC005_LasRespuestasDeError_TambienTraenElCorrelationId_IgualAlDelCuerpo()
    {
        await using var host = await Crear((_, _) => throw new NoEncontradoException("no existe"));

        var respuesta = await host.Client.GetAsync("/x");
        var cuerpo = JsonHelper.Parse(await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Cid(respuesta), cuerpo.GetProperty("correlationId").GetString());
        Assert.True(Guid.TryParseExact(Cid(respuesta), "D", out _));
    }

    // ---- R2: LabRunId -----------------------------------------------------------------------

    [Fact]
    public async Task R2_LabRunIdValido_SePropagaATodosLosEventosDeLaPeticion()
    {
        await using var host = await Crear(async (ctx, _) =>
        {
            ctx.RequestServices.GetRequiredService<ILogger<RequestDiagnosticMiddlewareTests>>()
                .LogInformation("evento propio de la petición");
            await ctx.Response.WriteAsync("ok");
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.Add("X-Lab-Run-Id", "run-3f9a1c0b7d21");
        await host.Client.SendAsync(request);
        await host.Sink.EsperarFinalizacionesAsync();

        var eventos = host.Sink.Events.Where(e => e.Properties.ContainsKey("CorrelationId")).ToList();
        Assert.True(eventos.Count >= 2);
        Assert.All(eventos, e => Assert.Equal("run-3f9a1c0b7d21", CollectorSink.Texto(e, "LabRunId")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("con espacios")]
    [InlineData("raro;<script>")]
    public async Task R2_LabRunIdAusenteOInvalido_UsaSinRun(string? valor)
    {
        await using var host = await Crear();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        if (valor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Lab-Run-Id", valor);
        }

        await host.Client.SendAsync(request);
        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal("sin-run", CollectorSink.Texto(host.Sink.Completions.Single(), "LabRunId"));
    }

    [Fact]
    public async Task R2_LabRunIdDeMasDe64Caracteres_UsaSinRun()
    {
        await using var host = await Crear();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.Add("X-Lab-Run-Id", new string('a', 65));
        await host.Client.SendAsync(request);
        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal("sin-run", CollectorSink.Texto(host.Sink.Completions.Single(), "LabRunId"));
    }

    // ---- R3 / INC-008: tiempo ---------------------------------------------------------------

    [Fact]
    public async Task INC008_ElapsedMs_ReflejaLaDuracionMedidaConElReloj_NoUnValorFijo()
    {
        await using var host = await Crear((ctx, reloj) =>
        {
            reloj.AdvanceMs(250);
            return ctx.Response.WriteAsync("ok");
        });
        await host.Client.GetAsync("/lento");
        await host.Sink.EsperarFinalizacionesAsync();

        var primero = ((ScalarValue)host.Sink.Completions.Single().Properties["ElapsedMs"]).Value;
        Assert.IsType<double>(primero);
        Assert.Equal(250.0, (double)primero!);

        await using var otro = await Crear((ctx, reloj) =>
        {
            reloj.AdvanceMs(800);
            return ctx.Response.WriteAsync("ok");
        });
        await otro.Client.GetAsync("/lento");
        await otro.Sink.EsperarFinalizacionesAsync();

        Assert.Equal(800.0, (double)((ScalarValue)otro.Sink.Completions.Single().Properties["ElapsedMs"]).Value!);
    }

    // ---- R5 / R8: un único evento de finalización, sin datos sensibles ----------------------

    [Fact]
    public async Task R5_UnaPeticion_ProduceExactamenteUnEventoDeFinalizacion_ConLasPropiedadesMinimas()
    {
        await using var host = await Crear();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pedidos?token=SECRETO-QUERY");
        request.Headers.Add("Authorization", "Bearer SECRETO-TOKEN");
        request.Content = new StringContent("{\"password\":\"SECRETO-BODY\"}");
        await host.Client.SendAsync(request);
        await host.Sink.EsperarFinalizacionesAsync();

        var evento = host.Sink.Completions.Single();
        Assert.Equal(LogEventLevel.Information, evento.Level);
        Assert.Equal("HTTP {RequestMethod} {RequestPath} finalizó con {StatusCode}", evento.MessageTemplate.Text);
        Assert.Equal("POST", CollectorSink.Texto(evento, "RequestMethod"));
        Assert.Equal("/api/pedidos", CollectorSink.Texto(evento, "RequestPath"));   // sin query string
        Assert.Equal(200, (int)((ScalarValue)evento.Properties["StatusCode"]).Value!);

        var todo = string.Join("\n", host.Sink.Events.Select(e => e.RenderMessage() + string.Join(",", e.Properties)));
        Assert.DoesNotContain("SECRETO", todo);
    }

    [Theory]
    [InlineData(200, LogEventLevel.Information)]
    [InlineData(201, LogEventLevel.Information)]
    [InlineData(400, LogEventLevel.Warning)]
    [InlineData(404, LogEventLevel.Warning)]
    [InlineData(409, LogEventLevel.Warning)]
    public async Task R5_ElNivelDelEventoDeFinalizacion_DependeDelEstadoFinal(int status, LogEventLevel nivel)
    {
        await using var host = await Crear((ctx, _) =>
        {
            ctx.Response.StatusCode = status;
            return ctx.Response.WriteAsync("x");
        });

        await host.Client.GetAsync("/x");
        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal(nivel, host.Sink.Completions.Single().Level);
    }

    // ---- R4 / R5 / R7: excepciones ----------------------------------------------------------

    [Fact]
    public async Task INC004_ExcepcionNoControlada_Responde500Seguro_YSeRegistraUnaSolaVezConSuDetalle()
    {
        await using var host = await Crear((_, _) => throw new InvalidOperationException("Fallo simulado de laboratorio"));

        var respuesta = await host.Client.GetAsync("/api/lab/excepcion");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.DoesNotContain("Fallo simulado", cuerpo);
        Assert.DoesNotContain("InvalidOperationException", cuerpo);

        var conExcepcion = host.Sink.Events.Where(e => e.Exception is not null).ToList();
        var unica = Assert.Single(conExcepcion);
        Assert.Equal(LogEventLevel.Error, unica.Level);
        Assert.Contains("Fallo simulado", unica.Exception!.ToString());
        Assert.Equal(Cid(respuesta), CollectorSink.Texto(unica, "CorrelationId"));

        var finalizacion = host.Sink.Completions.Single();
        Assert.Equal(500, (int)((ScalarValue)finalizacion.Properties["StatusCode"]).Value!);   // el estado final, tras el manejo
        Assert.Equal(LogEventLevel.Error, finalizacion.Level);
        Assert.Null(finalizacion.Exception);   // el detalle va en el registro adicional, no se duplica
    }

    [Fact]
    public async Task ErrorDeNegocio_NoSeRegistraComoError_NiComoExcepcionInterna()
    {
        await using var host = await Crear((_, _) => throw new ConflictoException("sin stock", "Stock insuficiente"));

        var respuesta = await host.Client.GetAsync("/x");
        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.DoesNotContain(host.Sink.Events, e => e.Level >= LogEventLevel.Error);
        Assert.DoesNotContain(host.Sink.Events, e => e.Exception is not null);
        Assert.Equal(LogEventLevel.Warning, host.Sink.Completions.Single().Level);
    }

    [Fact]
    public async Task R7_ExcepcionConLaRespuestaYaIniciada_NoReejecutaElPipeline_NiRegistraDoble()
    {
        await using var host = await Crear(async (ctx, _) =>
        {
            await ctx.Response.WriteAsync("inicio de la respuesta");
            await ctx.Response.Body.FlushAsync();   // a partir de aquí ya no se puede cambiar el estado
            throw new InvalidOperationException("fallo a mitad de la respuesta");
        });

        try
        {
            using var respuesta = await host.Client.GetAsync("/x", HttpCompletionOption.ResponseHeadersRead);
            await respuesta.Content.ReadAsStringAsync();
        }
        catch (Exception)
        {
            // La conexión se corta a propósito para que el cliente no tome por completa una respuesta truncada.
        }

        await host.Sink.EsperarFinalizacionesAsync();

        Assert.Equal(1, host.Invocaciones);
        var conExcepcion = host.Sink.Events.Where(e => e.Exception is not null).ToList();
        Assert.True(
            conExcepcion.Count == 1,
            "Eventos con Exception: " + string.Join(" | ", conExcepcion.Select(e => $"{e.Level} {e.MessageTemplate.Text} [{CollectorSink.Texto(e, "SourceContext")}]")));
        var finalizacion = host.Sink.Completions.Single();
        Assert.Equal(LogEventLevel.Error, finalizacion.Level);
    }
}
