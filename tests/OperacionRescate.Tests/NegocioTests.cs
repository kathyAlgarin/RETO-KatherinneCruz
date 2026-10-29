using Microsoft.Extensions.Logging.Abstractions;
using OperacionRescate.Api.Controllers;
using OperacionRescate.Api.Exceptions;
using OperacionRescate.Api.Models;
using OperacionRescate.Api.Services;

namespace OperacionRescate.Tests;

/// <summary>Reglas de negocio (INC-001, INC-002, INC-003). Cada prueba crea su propio inventario.</summary>
public class NegocioTests
{
    private readonly InventarioService _inventario = new(NullLogger<InventarioService>.Instance);
    private readonly PedidoService _pedidos;

    public NegocioTests()
    {
        _pedidos = new PedidoService(_inventario, NullLogger<PedidoService>.Instance);
    }

    private static CrearPedidoRequest Pedido(params (string Sku, int Cantidad)[] items) => new()
    {
        ClienteId = "CLI-001",
        Items = items.Select(i => new ItemPedidoRequest { Sku = i.Sku, Cantidad = i.Cantidad }).ToList(),
    };

    private int Stock(string sku) => _inventario.ObtenerPorSku(sku).Stock;

    // ---- INC-001: recurso no encontrado -> 404 ---------------------------------------------

    [Fact]
    public void INC001_ProductoInexistente_LanzaNoEncontradoConCodigo404()
    {
        var ex = Assert.Throws<NoEncontradoException>(() => _inventario.ObtenerPorSku("NOEXISTE-999"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("NOEXISTE-999", ex.Message);
    }

    [Fact]
    public void INC001_PedidoInexistente_LanzaNoEncontradoEnVezDeDevolverNull()
    {
        var controlador = new PedidosController(_pedidos);

        var ex = Assert.Throws<NoEncontradoException>(() => controlador.Obtener("no-existe"));

        Assert.Equal(404, ex.StatusCode);
    }

    // ---- INC-002: validación -> 400, sin alterar inventario --------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void INC002_CantidadNoPositiva_Lanza400_YNoAlteraElStock(int cantidad)
    {
        var antes = Stock("LAP-001");

        var ex = Assert.Throws<ValidacionException>(() => _pedidos.CrearPedido(Pedido(("LAP-001", cantidad))));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(antes, Stock("LAP-001"));
    }

    [Fact]
    public void INC002_ItemsVacio_Lanza400()
    {
        var ex = Assert.Throws<ValidacionException>(() => _pedidos.CrearPedido(Pedido()));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void INC002_ClienteVacio_Lanza400_YNoDescuentaStock()
    {
        var request = Pedido(("LAP-001", 1));
        request.ClienteId = "  ";

        Assert.Throws<ValidacionException>(() => _pedidos.CrearPedido(request));
        Assert.Equal(500, Stock("LAP-001"));
    }

    [Fact]
    public void INC002_CuerpoNulo_Lanza400_EnVezDeNullReference()
    {
        // JSON malformado o cantidad no entera: el binder entrega null.
        Assert.Throws<ValidacionException>(() => _pedidos.CrearPedido(null!));
    }

    [Fact]
    public void INC002_SkuInexistenteEnPedido_Lanza404_YNoDescuentaLosDemasItems()
    {
        var ex = Assert.Throws<NoEncontradoException>(
            () => _pedidos.CrearPedido(Pedido(("LAP-001", 1), ("NOEXISTE-000", 1))));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(500, Stock("LAP-001"));
    }

    // ---- INC-003: stock insuficiente -> 409 y atomicidad -----------------------------------

    [Fact]
    public void INC003_ExcesoDeInventario_Lanza409ConMensajeClaro_YNoAlteraElStock()
    {
        var ex = Assert.Throws<ConflictoException>(() => _pedidos.CrearPedido(Pedido(("SSD-005", 9))));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("Stock insuficiente", ex.Title);
        Assert.Equal("No hay unidades suficientes de SSD-005.", ex.Message);
        Assert.Equal(8, Stock("SSD-005"));
    }

    [Fact]
    public void INC003_PedidoAtomico_SiUnItemFalta_NingunItemSeDescuenta()
    {
        Assert.Throws<ConflictoException>(
            () => _pedidos.CrearPedido(Pedido(("LAP-001", 1), ("MOU-002", 1), ("SSD-005", 9))));

        Assert.Equal(500, Stock("LAP-001"));
        Assert.Equal(1000, Stock("MOU-002"));
        Assert.Equal(8, Stock("SSD-005"));
    }

    [Fact]
    public void INC003_MismoSkuRepetido_SeSumaAntesDeComprobarElStock()
    {
        // 300 + 300 > 500: cada ítem por separado cabe, juntos no.
        Assert.Throws<ConflictoException>(() => _pedidos.CrearPedido(Pedido(("LAP-001", 300), ("LAP-001", 300))));

        Assert.Equal(500, Stock("LAP-001"));
    }

    [Fact]
    public void INC003_PedidoPorExactamenteElStockDisponible_EsValido()
    {
        var pedido = _pedidos.CrearPedido(Pedido(("SSD-005", 8)));

        Assert.Equal(0, Stock("SSD-005"));
        Assert.Equal(872.00m, pedido.Total);
    }

    // ---- Lo que ya funcionaba debe seguir igual ---------------------------------------------

    [Fact]
    public void PedidoValido_DescuentaStock_CalculaTotal_YSePuedeConsultar()
    {
        var pedido = _pedidos.CrearPedido(Pedido(("LAP-001", 2), ("MOU-002", 1)));

        Assert.Equal("Confirmado", pedido.Estado);
        Assert.Equal(899.90m * 2 + 19.90m, pedido.Total);
        Assert.Equal(498, Stock("LAP-001"));
        Assert.Equal(999, Stock("MOU-002"));
        Assert.Equal(899.90m, pedido.Items[0].PrecioUnitario);
        Assert.Same(pedido, _pedidos.ObtenerPorId(pedido.Id));
    }
}
