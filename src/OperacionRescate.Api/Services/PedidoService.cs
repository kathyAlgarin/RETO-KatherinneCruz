using System.Collections.Concurrent;
using OperacionRescate.Api.Exceptions;
using OperacionRescate.Api.Models;

namespace OperacionRescate.Api.Services;

public class PedidoService : IPedidoService
{
    private readonly ConcurrentDictionary<string, Pedido> _pedidos = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly IInventarioService _inventario;
    private readonly ILogger<PedidoService> _logger;

    public PedidoService(IInventarioService inventario, ILogger<PedidoService> logger)
    {
        _inventario = inventario;
        _logger = logger;
    }

    public Pedido CrearPedido(CrearPedidoRequest request)
    {
        // 1) Validar la forma de la solicitud antes de tocar nada (400).
        Validar(request);

        lock (_sync)
        {
            var items = request.Items;
            _logger.LogInformation(
                "Procesando pedido de {ClienteId} con {CantidadItems} ítems, primer SKU {PrimerSku}",
                request.ClienteId, items.Count, items[0].Sku);

            // 2) Resolver todos los productos: un SKU inexistente es un 404, no una NullReference.
            var productos = items
                .Select(item => _inventario.ObtenerProductoParaPedido(item.Sku)
                    ?? throw new NoEncontradoException($"No existe el producto con SKU '{item.Sku}'."))
                .ToList();

            // 3) Comprobar el stock de TODOS los ítems sin modificar nada (atomicidad).
            //    Un mismo SKU repetido en varios ítems se suma (long: evita desbordar int).
            var solicitadoPorProducto = new Dictionary<Producto, long>();
            for (var i = 0; i < items.Count; i++)
            {
                solicitadoPorProducto[productos[i]] =
                    solicitadoPorProducto.GetValueOrDefault(productos[i]) + items[i].Cantidad;
            }

            foreach (var (producto, solicitado) in solicitadoPorProducto)
            {
                if (solicitado > producto.Stock)
                {
                    throw new ConflictoException(
                        $"No hay unidades suficientes de {producto.Sku}.", "Stock insuficiente");
                }
            }

            // 4) Recién ahora se descuenta: ya no puede fallar a mitad de camino.
            var total = 0m;
            var cantidadTotal = 0;
            for (var i = 0; i < items.Count; i++)
            {
                total += productos[i].Precio * items[i].Cantidad;
                cantidadTotal += items[i].Cantidad;
            }

            foreach (var (producto, solicitado) in solicitadoPorProducto)
            {
                producto.Stock -= (int)solicitado;
            }

            // cantidadTotal >= 1 garantizado por Validar: la división ya no puede fallar.
            var precioPromedio = total / cantidadTotal;
            _logger.LogInformation(
                "Precio promedio por unidad del pedido de {ClienteId}: {PrecioPromedio}",
                request.ClienteId, precioPromedio);

            var pedido = new Pedido
            {
                Id = Guid.NewGuid().ToString(),
                ClienteId = request.ClienteId,
                Estado = "Confirmado",
                Total = total,
                Items = items.Select((item, i) => new ItemPedido
                {
                    Sku = item.Sku,
                    Cantidad = item.Cantidad,
                    PrecioUnitario = productos[i].Precio
                }).ToList()
            };

            _pedidos[pedido.Id] = pedido;

            _logger.LogInformation("Pedido {PedidoId} procesado correctamente", pedido.Id);

            return pedido;
        }
    }

    public Pedido? ObtenerPorId(string id)
    {
        _pedidos.TryGetValue(id, out var pedido);
        return pedido;
    }

    private static void Validar(CrearPedidoRequest? request)
    {
        if (request is null)
        {
            throw new ValidacionException("El cuerpo de la solicitud es obligatorio y debe ser un JSON válido.");
        }

        if (string.IsNullOrWhiteSpace(request.ClienteId))
        {
            throw new ValidacionException("El campo 'clienteId' es obligatorio.");
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ValidacionException("El pedido debe incluir al menos un ítem.");
        }

        foreach (var item in request.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Sku))
            {
                throw new ValidacionException("Cada ítem debe indicar un 'sku'.");
            }

            if (item.Cantidad <= 0)
            {
                throw new ValidacionException($"La cantidad de {item.Sku} debe ser un entero mayor que cero.");
            }
        }
    }
}
