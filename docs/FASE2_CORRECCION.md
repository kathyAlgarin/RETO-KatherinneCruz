# Fase 2 — Corrección y manejo de errores

Principio: **no se reescribe la aplicación**. Se corrigieron los archivos existentes y
solo se agregó lo necesario para separar *errores esperados* de *errores inesperados*.

## Diseño

| Pregunta guía | Decisión |
|---|---|
| ¿Qué diferencia a una excepción esperada de una inesperada? | Las esperadas heredan de `AppException` (`ValidacionException` 400, `NoEncontradoException` 404, `ConflictoException` 409) y llevan un mensaje seguro para el cliente. Todo lo demás (`InvalidOperationException`, `NullReferenceException`, …) es un fallo interno. |
| ¿Quién decide el código HTTP? | El tipo de excepción (`AppException.StatusCode`), traducido en **un único lugar**: `ErrorHandling/GlobalExceptionHandler` (`IExceptionHandler`). Los servicios no conocen HTTP más allá del código que declara su excepción. |
| ¿Qué ve el cliente y qué el equipo? | Cliente: `application/problem+json` con `type`, `title`, `status`, `detail`, `correlationId`; en un 500 solo un mensaje genérico. Equipo: tipo, mensaje y traza completa en los logs (solo para errores inesperados). |

## Cambios por archivo

| Archivo | Cambio | Incidente |
|---|---|---|
| `Exceptions/AppException.cs` (nuevo) | Jerarquía de errores esperados. | INC-001/002/003 |
| `ErrorHandling/ProblemResponse.cs` (nuevo) | Forma única de la respuesta de error (§5.5). | INC-004 |
| `ErrorHandling/GlobalExceptionHandler.cs` (nuevo) | Convierte excepciones en respuestas seguras; registra solo lo inesperado. | INC-004 |
| `Middleware/LegacyErrorMiddleware.cs` (eliminado) | Reemplazado: escribía `ex.ToString()` como `text/plain`, y trataba todo como 500 (`:22-28`). No se eliminó funcionalidad: solo el mecanismo defectuoso. | INC-004 |
| `Program.cs` | `AddExceptionHandler`, `UseExceptionHandler`, `UseStatusCodePages` (rutas desconocidas/415 también como problem+json). | INC-004 |
| `Services/InventarioService.cs:28-31` | `ObtenerPorSku` lanza `NoEncontradoException` en vez de dejar escapar `KeyNotFoundException`. Log interpolado → plantilla con propiedad. | INC-001 |
| `Controllers/PedidosController.cs:24-29` | `GET /api/pedidos/{id}` inexistente lanza `NoEncontradoException` (antes `Ok(null)` → 204). | INC-001 (caso cercano) |
| `Services/PedidoService.cs` | Se reescribió **el cuerpo de `CrearPedido`** (misma firma e interfaz): (1) `Validar` antes de tocar nada → 400; (2) SKU inexistente → 404; (3) se comprueba stock de **todos** los ítems (sumando SKU repetidos) antes de descontar → 409 sin efectos; (4) recién entonces se descuenta. El precio promedio (la división que fallaba) se conserva, pero ya es seguro porque `cantidadTotal ≥ 1`. | INC-002, INC-003 |

## Por qué la solución es la correcta

- **Atomicidad:** el original descontaba en el bucle de `PedidoService.cs:40-44` y verificaba
  después (`:46-53`). Ahora el orden es validar → resolver → verificar stock → mutar, todo bajo
  el mismo `lock`, de modo que ningún rechazo puede dejar el inventario modificado.
- **Cantidad negativa:** era peor que el síntoma descrito (subía el stock en vez de fallar). Se
  rechaza en `Validar` con 400 antes de cualquier cálculo.
- **SKU repetido:** `[{LAP-001,300},{LAP-001,300}]` con stock 500 debe ser 409; comprobar cada
  ítem por separado lo habría dejado pasar. Se agrupa por producto.
- **Lo que ya funcionaba sigue igual:** listado, consulta insensible a mayúsculas, pedido válido
  con `Location`, `/api/lab/*` sin cambios (`LabController` no se tocó).

## Verificación (Fase 2, antes del middleware y Serilog)

Se reejecutó `evidencias/reproducir.sh`: 404 en producto/pedido/SKU inexistente, 400 en
cantidad 0, -5, 1.5, items vacío, clienteId vacío y JSON malformado; 409 en exceso de inventario;
stock sin cambios tras todos los rechazos (LAP-001=500, SSD-005=8, MOU-002=1000); 500 genérico
sin traza en `/api/lab/excepcion`; y el pedido válido sigue dando 201.

Pendiente de las fases siguientes: `correlationId` aún usa el `TraceIdentifier` de ASP.NET
(sin `X-Correlation-ID`); los logs aún no van a archivo.
