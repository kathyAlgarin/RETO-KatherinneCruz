using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OperacionRescate.Tests;

/// <summary>
/// La aplicación real completa (controladores + middleware + Serilog con archivo) en memoria.
/// Cada prueba levanta su propia aplicación: inventario y carpeta de logs independientes.
/// </summary>
public sealed class ApiEndToEndTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "operacion-rescate-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiEndToEndTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("LogDirectory", _logDir));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo: el sistema operativo puede tardar en soltar el archivo.
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<int> StockAsync(string sku)
        => (await _client.GetFromJsonAsync<JsonElement>($"/api/inventario/{sku}")).GetProperty("stock").GetInt32();

    private static async Task AssertProblemAsync(HttpResponseMessage r, int status)
    {
        Assert.Equal((HttpStatusCode)status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        var cuerpo = await r.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal(status, json.GetProperty("status").GetInt32());
        Assert.Equal(r.Headers.GetValues("X-Correlation-ID").Single(), json.GetProperty("correlationId").GetString());
        foreach (var prohibido in new[] { "StackTrace", " at ", "System.", "Microsoft.", ".cs", "Exception", @"C:\" })
        {
            Assert.DoesNotContain(prohibido, cuerpo);
        }
    }

    [Fact]
    public async Task INC001_ProductoInexistente_Responde404ProblemJson()
        => await AssertProblemAsync(await _client.GetAsync("/api/inventario/NOEXISTE-999"), 404);

    [Fact]
    public async Task INC001_PedidoInexistente_Responde404EnVezDe204()
        => await AssertProblemAsync(await _client.GetAsync("/api/pedidos/no-existe"), 404);

    [Theory]
    [InlineData("""{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":0}]}""")]
    [InlineData("""{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":-5}]}""")]
    [InlineData("""{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":1.5}]}""")]
    [InlineData("""{"clienteId":"CLI-001","items":[]}""")]
    [InlineData("""{"clienteId":"","items":[{"sku":"LAP-001","cantidad":1}]}""")]
    [InlineData("""{"clienteId":"CLI-001","items":[""")]
    public async Task INC002_PedidoInvalido_Responde400_YNoAlteraElInventario(string cuerpo)
    {
        var antes = await StockAsync("LAP-001");

        await AssertProblemAsync(await _client.PostAsync("/api/pedidos", Json(cuerpo)), 400);

        Assert.Equal(antes, await StockAsync("LAP-001"));
    }

    [Fact]
    public async Task INC002_SkuInexistenteEnPedido_Responde404()
        => await AssertProblemAsync(
            await _client.PostAsync("/api/pedidos", Json("""{"clienteId":"CLI-001","items":[{"sku":"NOEXISTE-000","cantidad":1}]}""")), 404);

    [Fact]
    public async Task INC003_ExcesoDeInventario_Responde409_YNoAlteraNingunStock()
    {
        var respuesta = await _client.PostAsync("/api/pedidos", Json(
            """{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":1},{"sku":"SSD-005","cantidad":9}]}"""));

        await AssertProblemAsync(respuesta, 409);
        Assert.Equal(500, await StockAsync("LAP-001"));
        Assert.Equal(8, await StockAsync("SSD-005"));
    }

    [Fact]
    public async Task INC004_ExcepcionDeLaboratorio_Responde500GenericoSinDetallesTecnicos()
        => await AssertProblemAsync(await _client.GetAsync("/api/lab/excepcion"), 500);

    [Fact]
    public async Task EndpointsDeLaboratorio_ConservanSuComportamiento()
    {
        var lento = await _client.GetFromJsonAsync<JsonElement>("/api/lab/lento?ms=30");
        var fueraDeRango = await _client.GetFromJsonAsync<JsonElement>("/api/lab/lento?ms=999999");

        Assert.Equal(30, lento.GetProperty("esperadoMs").GetInt32());
        Assert.Equal(2000, fueraDeRango.GetProperty("esperadoMs").GetInt32());
    }

    [Fact]
    public async Task PedidoValido_Responde201ConLocation_DescuentaStock_YSeConsulta()
    {
        var respuesta = await _client.PostAsync("/api/pedidos", Json("""{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":2}]}"""));
        var pedido = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.NotNull(respuesta.Headers.Location);
        Assert.Equal(1799.80m, pedido.GetProperty("total").GetDecimal());
        Assert.Equal(498, await StockAsync("LAP-001"));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/pedidos/{pedido.GetProperty("id").GetString()}")).StatusCode);
    }

    // ---- INC-006: los eventos terminan realmente en archivos JSON Lines ----------------------

    [Fact]
    public async Task INC006_LosEventosSeEscribenEnArchivosJsonLines_ConLosCamposObligatorios()
    {
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventario/LAP-001?token=NO-DEBE-APARECER");
        request.Headers.Add("X-Correlation-ID", correlationId);
        request.Headers.Add("X-Lab-Run-Id", "run-prueba-e2e");
        await _client.SendAsync(request);
        await _client.GetAsync("/api/lab/excepcion");

        var lineas = await LeerLineasConAsync(correlationId);

        Assert.True(Directory.GetFiles(_logDir, "operacion-rescate-*.jsonl").Length >= 1);
        var evento = JsonDocument.Parse(lineas.Single(l => l.Contains("ElapsedMs"))).RootElement;
        Assert.Equal("/api/inventario/LAP-001", evento.GetProperty("RequestPath").GetString());
        Assert.Equal("GET", evento.GetProperty("RequestMethod").GetString());
        Assert.Equal(200, evento.GetProperty("StatusCode").GetInt32());
        Assert.True(evento.GetProperty("ElapsedMs").GetDouble() >= 0);
        Assert.Equal("run-prueba-e2e", evento.GetProperty("LabRunId").GetString());
        Assert.EndsWith("Z", evento.GetProperty("Timestamp").GetString());

        var todoElArchivo = string.Concat(Directory.GetFiles(_logDir).Select(LeerCompartido));
        Assert.DoesNotContain("NO-DEBE-APARECER", todoElArchivo);
        Assert.All(todoElArchivo.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => JsonDocument.Parse(l));
        Assert.Single(
            todoElArchivo.Split('\n'),
            l => l.Contains("Fallo simulado de laboratorio") && l.Contains("\"Exception\""));
    }

    private async Task<List<string>> LeerLineasConAsync(string correlationId)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            var lineas = Directory.Exists(_logDir)
                ? Directory.GetFiles(_logDir).SelectMany(f => LeerCompartido(f).Split('\n')).Where(l => l.Contains(correlationId)).ToList()
                : [];
            if (lineas.Any(l => l.Contains("ElapsedMs")))
            {
                return lineas;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("El evento de finalización no apareció en el archivo de logs.");
    }

    // El sumidero de archivo mantiene el archivo abierto: se lee compartiendo escritura.
    private static string LeerCompartido(string ruta)
    {
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
