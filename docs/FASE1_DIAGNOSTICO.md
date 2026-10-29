# Fase 1 — Diagnóstico (antes de corregir)

Código recibido sin modificar (commit `b29e6c4`). La API se ejecutó tal cual con
`dotnet run --project src/OperacionRescate.Api --urls http://localhost:5000` y se
reprodujo cada incidente con `curl` mediante `evidencias/reproducir.sh`.

Evidencia cruda (petición, código, cabeceras, cuerpo): `evidencias/antes/*.txt`.
Salida de consola de la API durante la reproducción: `evidencias/antes/consola-api.txt`.
Resumen de la corrida: `evidencias/antes/resumen-reproduccion.txt`.

## Hallazgos por incidente

| Incidente | Cómo lo reproduje | Respuesta observada | Causa raíz (archivo:línea) |
|---|---|---|---|
| **INC-001** producto inexistente | `GET /api/inventario/NOEXISTE-999` | `500`, `text/plain`, `KeyNotFoundException` con traza completa | `InventarioService.cs:30` usa el indexador `_productos[sku]`, que lanza `KeyNotFoundException` si la clave no existe; el contrato del método (`Producto`, no nulo) no tiene forma de expresar "no encontrado". `InventarioController.cs:25` hace `Ok(...)` sin decidir 404. |
| **INC-001b** pedido inexistente (caso cercano) | `GET /api/pedidos/no-existe` | `204 No Content` (debía ser 404) | `PedidosController.cs:27-28` hace `Ok(pedido)` con `pedido == null` (`PedidoService.ObtenerPorId` devuelve `Pedido?`); MVC convierte `Ok(null)` en 204. |
| **INC-002** cantidad cero | `POST /api/pedidos` con `cantidad: 0` | `500` `DivideByZeroException` | `PedidoService.cs:37` calcula `total / cantidadTotal` (precio promedio solo para un log) y no hay validación previa de la cantidad. |
| **INC-002b** cantidad negativa | `POST` con `cantidad: -5` | **`201 Created`** y el stock de LAP-001 **subió** de 500 a 505 | `PedidoService.cs:43` hace `producto.Stock -= item.Cantidad`: restar un negativo suma. Nunca se valida `cantidad > 0`. Es peor que el síntoma descrito: corrompe el inventario. |
| **INC-002c/d/e/f/g** entradas inválidas cercanas | cantidad `1.5`; `items: []`; `clienteId: ""`; JSON malformado; SKU inexistente | `500` (`NullReferenceException` o `InvalidOperationException`) en todos salvo `clienteId` vacío, que devuelve **`201`** | (a) `PedidosController.cs:7-18`: sin `[ApiController]`, un cuerpo inválido no produce 400 automático; `request` llega `null` y `PedidoService.cs:23` lanza NRE. (b) `PedidoService.cs:24` `items.First()` falla con lista vacía. (c) `PedidoService.cs:32` usa `!` sobre `ObtenerProductoParaPedido`, que devuelve `null` si el SKU no existe → NRE en vez de 404. (d) Nadie valida `ClienteId`. |
| **INC-003** exceso de inventario | `POST` 9 × SSD-005 (stock 8) | `500` `System.Exception: Error al procesar el pedido`; el stock de SSD-005 quedó en **-1** | `PedidoService.cs:40-44` descuenta el stock **antes** de verificar y `PedidoService.cs:46-53` verifica después y lanza una `Exception` genérica sin revertir. El pedido no es atómico: no se comprueba disponibilidad antes de modificar nada y no existe un tipo de excepción de conflicto. |
| **INC-003b** atomicidad | `POST` 1 LAP-001 + 1 MOU-002 + 9 SSD-005 | `500`; LAP-001 y MOU-002 **perdieron** unidades aunque el pedido falló; SSD-005 quedó en -10 | Misma causa: mutación parcial sin rollback (`PedidoService.cs:40-53`). |
| **INC-004** detalles técnicos | `GET /api/lab/excepcion` | `500`, `text/plain`, cuerpo = `ex.ToString()` con tipo, mensaje, rutas del disco (`C:\Users\...`) y espacios de nombres | `LegacyErrorMiddleware.cs:26-28`: fija `text/plain` y escribe `ex.ToString()` en la respuesta. Además captura **toda** excepción como 500 (`:20`), sin distinguir errores esperados. |
| **INC-005** correlación | `GET` con y sin `X-Correlation-ID` (válido y hostil) | La respuesta **nunca** trae `X-Correlation-ID`; el valor enviado se ignora | No existe ningún componente que lea/genere el identificador (`Program.cs:13` solo registra `LegacyErrorMiddleware`). El log de error (`LegacyErrorMiddleware.cs:22`) solo escribe `"Error: " + ex.Message`: sin id, sin ruta, sin traza. Imposible relacionar petición y registro. |
| **INC-006** logs no almacenados | Revisión de `Program.cs`, `appsettings*.json`, y carpeta `logs/` | No existe `logs/` tras toda la corrida; la salida solo aparece en consola | `Program.cs` no configura ningún sumidero a archivo ni Serilog; solo el `ILogger` por defecto (consola). Los `appsettings` solo fijan niveles. |
| **INC-007** logs difíciles de analizar | Lectura de `consola-api.txt` | Texto plano: `Error: ...`, `Procesando pedido de CLI-001 con primer SKU ...` | `InventarioService.cs:20`, `PedidoService.cs:25,38,75` y `LegacyErrorMiddleware.cs:22` usan **interpolación de cadenas** (`$"..."` o concatenación) en `LogXxx`, por lo que los valores se pierden como texto y no quedan como propiedades estructuradas. No hay formato JSON. |
| **INC-008** tiempos | `GET /api/lab/lento?ms=800` | Ningún registro de duración en consola ni archivo | Nada mide el tiempo de la petición completa; no hay evento de finalización por petición. |

## Comportamiento correcto previo que debe conservarse

Verificado en `evidencias/antes/OK-0*.txt`:

- `GET /api/inventario` → `200` con los 5 productos (orden por SKU).
- `GET /api/inventario/lap-001` → `200`; la búsqueda ignora mayúsculas.
- `POST /api/pedidos` válido → `201`, `total` correcto, cabecera `Location` y descuento de stock.
- `GET /api/lab/lento?ms=N` → `200 {"esperadoMs": N}` (con `Math.Clamp` a 0–2000).
- `GET /api/lab/excepcion` debe seguir **lanzando** `InvalidOperationException("Fallo simulado de laboratorio")`; solo cambia cómo se responde.

## Observaciones transversales

- Los incidentes están encadenados: INC-002/003 son síntomas de falta de validación y
  de orden incorrecto (mutar antes de validar); INC-004 es consecuencia del
  middleware legado; INC-005/008 son consecuencia de la ausencia de un middleware de
  diagnóstico; INC-006/007 son consecuencia de no haber configurado Serilog.
- El estado del inventario tras la reproducción (`LAP-001`=501, `SSD-005`=-10) muestra
  que los defectos de negocio dejan **datos corruptos**, no solo respuestas feas.

## Plan de corrección (Fases 2–4)

1. **Fase 2:** jerarquía de excepciones de aplicación (`ValidacionException` 400,
   `NoEncontradoException` 404, `ConflictoException` 409), validación completa antes de
   mutar, comprobación de stock de **todos** los ítems antes de descontar (atomicidad),
   `[ApiController]` + `IExceptionHandler` + ProblemDetails global; retirar
   `LegacyErrorMiddleware`.
2. **Fase 3:** `RequestDiagnosticMiddleware` (R1–R8).
3. **Fase 4:** Serilog con `Serilog.AspNetCore` + sumidero a archivo y formateador JSON
   Lines propio (propiedades en el nivel superior, UTC con `Z`).
4. **Fase 5:** pruebas xUnit, validador, evidencias "después", informe.
