#!/usr/bin/env bash
# Reproduce los incidentes INC-001..INC-008 con peticiones reales y guarda
# peticion + respuesta completa (codigo, cabeceras, cuerpo) en un archivo por caso.
#
# Uso:  bash evidencias/reproducir.sh [BASE_URL] [CARPETA_SALIDA]
# Ej.:  bash evidencias/reproducir.sh http://localhost:5000 evidencias/antes
#
# No es el validador del docente: es el script propio usado para el diagnostico
# (Fase 1, "antes") y para la comparacion "despues" (Fase 5).

BASE="${1:-http://localhost:5000}"
OUT="${2:-evidencias/antes}"
mkdir -p "$OUT"

# caso NOMBRE "descripcion" METODO RUTA [CUERPO] [CABECERA...]
caso() {
  local nombre="$1" desc="$2" metodo="$3" ruta="$4" cuerpo="${5-}"
  shift 4; [[ $# -gt 0 ]] && shift
  local args=(-s -i -m 30 -X "$metodo")
  local h
  for h in "$@"; do args+=(-H "$h"); done
  [[ -n "$cuerpo" ]] && args+=(-H "Content-Type: application/json" --data-binary "$cuerpo")
  {
    echo "# $nombre - $desc"
    echo "# Peticion: $metodo $ruta"
    [[ -n "$cuerpo" ]] && echo "# Cuerpo:   $cuerpo"
    for h in "$@"; do echo "# Cabecera: $h"; done
    echo "# ---------------- respuesta ----------------"
    curl "${args[@]}" "$BASE$ruta"
    echo
  } > "$OUT/$nombre.txt" 2>&1
  echo "[$nombre] $metodo $ruta -> $(head -n 6 "$OUT/$nombre.txt" | grep -m1 '^HTTP')"
}

stock() { curl -s "$BASE/api/inventario/$1" | grep -o '"stock":-\?[0-9]*' ; }

echo "== Stock inicial: LAP-001 $(stock LAP-001) | SSD-005 $(stock SSD-005)"

caso INC-001_producto-inexistente "GET de SKU que no existe" GET /api/inventario/NOEXISTE-999
caso INC-001b_pedido-inexistente  "GET de pedido que no existe" GET /api/pedidos/no-existe

caso INC-002_cantidad-cero    "POST con cantidad 0" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":0}]}'
S0="$(stock LAP-001)"
caso INC-002b_cantidad-negativa "POST con cantidad -5 (debe rechazar y no tocar stock)" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":-5}]}'
echo "   stock LAP-001 antes=$S0 despues=$(stock LAP-001)"
caso INC-002c_cantidad-decimal  "POST con cantidad 1.5 (no entera)" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":1.5}]}'
caso INC-002d_items-vacio       "POST con items vacio" POST /api/pedidos '{"clienteId":"CLI-001","items":[]}'
caso INC-002e_cliente-vacio     "POST con clienteId vacio" POST /api/pedidos '{"clienteId":"","items":[{"sku":"LAP-001","cantidad":1}]}'
caso INC-002f_json-malformado   "POST con JSON malformado" POST /api/pedidos '{"clienteId":"CLI-001","items":['
caso INC-002g_sku-inexistente   "POST con SKU inexistente" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"NOEXISTE-000","cantidad":1}]}'

SS="$(stock SSD-005)"
caso INC-003_exceso-inventario  "POST pidiendo 9 SSD-005 con stock 8" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"SSD-005","cantidad":9}]}'
echo "   stock SSD-005 antes=$SS despues=$(stock SSD-005)"
caso INC-003b_atomicidad        "POST 1 LAP-001 + 9 SSD-005: LAP-001 no debe bajar" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":1},{"sku":"MOU-002","cantidad":1},{"sku":"SSD-005","cantidad":9}]}'
echo "   stock tras atomicidad: LAP-001 $(stock LAP-001) | MOU-002 $(stock MOU-002) | SSD-005 $(stock SSD-005)"

caso INC-004_excepcion-interna  "GET /api/lab/excepcion expone detalles tecnicos" GET /api/lab/excepcion

caso INC-005_correlacion        "Sin X-Correlation-ID / con uno valido / con uno hostil" GET /api/inventario/MOU-002
caso INC-005b_correlacion-valido "X-Correlation-ID valido enviado" GET /api/inventario/MOU-002 "" "X-Correlation-ID: 8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f11"
caso INC-005c_correlacion-hostil "X-Correlation-ID malformado enviado" GET /api/inventario/MOU-002 "" "X-Correlation-ID: abc-8d2c5e2a-3a0e-4e3a-9d7a-0d0b3e3c9f11-zzz"

caso INC-008_lento "GET /api/lab/lento?ms=800 (el log no registra duracion)" GET "/api/lab/lento?ms=800"

caso OK-01_inventario-lista     "Comportamiento correcto previo: listado" GET /api/inventario
caso OK-02_inventario-existente "Comportamiento correcto previo: producto existente" GET /api/inventario/lap-001
caso OK-03_pedido-valido        "Comportamiento correcto previo: pedido valido" POST /api/pedidos '{"clienteId":"CLI-001","items":[{"sku":"LAP-001","cantidad":2}]}'
echo "== Stock final: LAP-001 $(stock LAP-001) | MOU-002 $(stock MOU-002) | SSD-005 $(stock SSD-005)"
