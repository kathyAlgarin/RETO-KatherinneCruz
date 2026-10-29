using System.Text.Json;
using System.Text.RegularExpressions;
using OperacionRescate.Api.Logging;
using OperacionRescate.Tests.Support;
using Serilog.Events;
using Serilog.Parsing;

namespace OperacionRescate.Tests;

/// <summary>INC-007: los logs son JSON Lines con propiedades en el nivel superior y timestamp UTC.</summary>
public class JsonLinesFormatterTests
{
    private static string Formatear(LogEvent evento)
    {
        using var writer = new StringWriter();
        new JsonLinesFormatter().Format(evento, writer);
        return writer.ToString();
    }

    private static LogEvent Evento(
        DateTimeOffset cuando, LogEventLevel nivel, Exception? ex, string plantilla, params (string Nombre, object Valor)[] props)
        => new(
            cuando, nivel, ex, new MessageTemplateParser().Parse(plantilla),
            props.Select(p => new LogEventProperty(p.Nombre, new ScalarValue(p.Valor))));

    [Fact]
    public void INC007_EscribeUnaSolaLinea_ConUnObjetoJsonValido()
    {
        var linea = Formatear(Evento(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            "HTTP {RequestMethod} {RequestPath} finalizó con {StatusCode}",
            ("RequestMethod", "GET"), ("RequestPath", "/api/inventario/LAP-001"), ("StatusCode", 200)));

        Assert.EndsWith("\n", linea);
        Assert.Single(linea.TrimEnd('\n').Split('\n'));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(linea).RootElement.ValueKind);
    }

    [Fact]
    public void INC007_LasPropiedadesVanEnElNivelSuperior_Y_StatusCodeYElapsedMsSonNumeros()
    {
        var json = JsonHelper.Parse(Formatear(Evento(
            DateTimeOffset.UtcNow, LogEventLevel.Warning, null,
            "HTTP {RequestMethod} {RequestPath} finalizó con {StatusCode}",
            ("RequestMethod", "GET"), ("RequestPath", "/x"), ("StatusCode", 404),
            ("ElapsedMs", 18.4), ("CorrelationId", "abc"), ("LabRunId", "run-1"))));

        Assert.False(json.TryGetProperty("Properties", out _));
        Assert.Equal(JsonValueKind.Number, json.GetProperty("StatusCode").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("ElapsedMs").ValueKind);
        Assert.Equal(18.4, json.GetProperty("ElapsedMs").GetDouble());
        Assert.Equal("Warning", json.GetProperty("Level").GetString());
        Assert.Equal("abc", json.GetProperty("CorrelationId").GetString());
        Assert.Equal("HTTP {RequestMethod} {RequestPath} finalizó con {StatusCode}", json.GetProperty("MessageTemplate").GetString());
    }

    [Fact]
    public void INC007_ElTimestampSeEscribeEnUtcIso8601ConZ_AunqueElEventoTengaOtraZonaHoraria()
    {
        var local = new DateTimeOffset(2026, 10, 29, 10, 0, 0, 123, TimeSpan.FromHours(-6));

        var json = JsonHelper.Parse(Formatear(Evento(local, LogEventLevel.Information, null, "x")));

        Assert.Equal("2026-10-29T16:00:00.123Z", json.GetProperty("Timestamp").GetString());
    }

    [Fact]
    public void INC007_LaExcepcionConservaTipoMensajeYTrazaCompleta()
    {
        Exception capturada;
        try
        {
            throw new InvalidOperationException("Fallo simulado de laboratorio");
        }
        catch (Exception ex)
        {
            capturada = ex;
        }

        var json = JsonHelper.Parse(Formatear(Evento(
            DateTimeOffset.UtcNow, LogEventLevel.Error, capturada, "Excepción no controlada en {RequestPath}", ("RequestPath", "/x"))));

        var texto = json.GetProperty("Exception").GetString()!;
        Assert.StartsWith("System.InvalidOperationException: Fallo simulado de laboratorio", texto);
        Assert.Matches(new Regex(@"\n\s+at "), texto);
    }

    [Fact]
    public void INC007_UnaPropiedadConNombreReservadoNoPisaLosCamposDelEvento()
    {
        var json = JsonHelper.Parse(Formatear(Evento(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null, "x {Level}", ("Level", "otra cosa"))));

        Assert.Equal("Information", json.GetProperty("Level").GetString());
        Assert.Equal("otra cosa", json.GetProperty("Prop_Level").GetString());
    }
}
