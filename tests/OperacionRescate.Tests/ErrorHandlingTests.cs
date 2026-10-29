using Microsoft.AspNetCore.Http;
using OperacionRescate.Api.ErrorHandling;
using OperacionRescate.Api.Exceptions;
using OperacionRescate.Tests.Support;

namespace OperacionRescate.Tests;

/// <summary>INC-004: el cliente recibe problem+json seguro; los detalles técnicos no salen del servidor.</summary>
public class ErrorHandlingTests
{
    private static async Task<(HttpContext Context, string Cuerpo)> ManejarAsync(Exception ex)
    {
        var context = new DefaultHttpContext();
        context.Items[DiagnosticItems.CorrelationId] = "8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f11";
        context.Response.Body = new MemoryStream();

        var manejado = await new GlobalExceptionHandler().TryHandleAsync(context, ex, CancellationToken.None);

        Assert.True(manejado);
        context.Response.Body.Position = 0;
        return (context, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task INC004_ExcepcionInesperada_Devuelve500GenericoSinDetallesTecnicos()
    {
        var (context, cuerpo) = await ManejarAsync(new InvalidOperationException("Fallo simulado de laboratorio"));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.DoesNotContain("Fallo simulado", cuerpo);
        Assert.DoesNotContain("InvalidOperationException", cuerpo);
        Assert.DoesNotContain("System.", cuerpo);
        Assert.DoesNotContain(" at ", cuerpo);
        Assert.DoesNotContain(".cs", cuerpo);

        var json = JsonHelper.Parse(cuerpo);
        Assert.Equal(500, json.GetProperty("status").GetInt32());
        Assert.Equal("8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f11", json.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task ErrorEsperado_ConservaSuCodigoYSuMensajeSeguro(int esperado)
    {
        const string detalle = "mensaje seguro para el cliente";
        Exception ex = esperado switch
        {
            400 => new ValidacionException(detalle),
            404 => new NoEncontradoException(detalle),
            _ => new ConflictoException(detalle),
        };

        var (context, cuerpo) = await ManejarAsync(ex);

        Assert.Equal(esperado, context.Response.StatusCode);
        var json = JsonHelper.Parse(cuerpo);
        Assert.Equal(esperado, json.GetProperty("status").GetInt32());
        Assert.Equal(detalle, json.GetProperty("detail").GetString());
        Assert.True(json.TryGetProperty("title", out _));
        Assert.True(json.TryGetProperty("type", out _));
    }
}
