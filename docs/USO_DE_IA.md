# Declaración de uso de herramientas de IA

**Herramienta:** Claude Code (modelo Claude Sonnet 5.5, Anthropic), usado como asistente
de programación en la aplicación de escritorio de Claude.

**Condición de la guía (§3):** el uso de IA se declara aquí y en el informe. Todo el
código debe poder explicarse y modificarse en vivo en la defensa; el código que no se
pueda explicar cuenta como no entregado.

## Qué se le pidió a la IA y qué hizo, por fase

| Fase | Uso de la IA | Qué verificó la persona / cómo se comprobó |
|---|---|---|
| Preparación | Leer la guía PDF y el repositorio; clonar el repo; proponer el plan por fases; crear una rama local (`fix/operacion-rescate`) y bloquear el `push` del remoto local para no subir nada. | Se revisó que la rama es local y que `git remote -v` muestra el push deshabilitado. |
| 1 — Diagnóstico | Escribir `evidencias/reproducir.sh` (peticiones `curl` por incidente), ejecutarlo contra la API **sin modificar**, localizar causas raíz con archivo/línea y redactar `docs/FASE1_DIAGNOSTICO.md`. | Las respuestas están guardadas tal cual en `evidencias/antes/`; cada causa se contrastó con la salida real (p. ej. el stock de SSD-005 en -1). |
| 2 — Corrección y errores | Diseño y escritura de las excepciones de aplicación, validación, atomicidad del pedido y manejador global (`IExceptionHandler`/ProblemDetails). | _(se completa al terminar la fase)_ |
| 3 — Middleware | Diseño y escritura de `RequestDiagnosticMiddleware`. | _(se completa al terminar la fase)_ |
| 4 — Serilog | Configuración de Serilog y del formateador JSON Lines propio. | _(se completa al terminar la fase)_ |
| 5 — Pruebas e informe | Escritura de pruebas xUnit, ejecución del validador y redacción del informe. | _(se completa al terminar la fase)_ |

## Límites respetados

- No se reemplazó la aplicación: se modificaron los archivos existentes y solo se
  agregaron los necesarios (excepciones, manejador, middleware, formateador de logs, pruebas).
- No se alteraron `validador/validar.ps1` ni `validador/validar.sh`.
- No se eliminaron ni se cambió el comportamiento de `/api/lab/*`.
- No se devuelven respuestas ficticias ni valores fijos para pasar las pruebas.
