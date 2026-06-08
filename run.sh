#!/usr/bin/env bash
# Lanza MonitorPedidos.Web con el content root correcto y entorno Development.
# Compila a bin/fresh-build (evita conflicto con Language Server de VS Code que bloquea bin/Debug).
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "[run.sh] Compilando..."
dotnet build "$SCRIPT_DIR/src/MonitorPedidos.Web/MonitorPedidos.Web.csproj" -o "$SCRIPT_DIR/bin/fresh-build" -q

# Matar instancia previa en :5000 si la hay
fuser -k 5000/tcp 2>/dev/null || true

echo "[run.sh] Iniciando MonitorPedidos en http://localhost:5000"
ASPNETCORE_ENVIRONMENT=Development \
  dotnet exec \
    --contentroot "$SCRIPT_DIR/src/MonitorPedidos.Web" \
    "$SCRIPT_DIR/bin/fresh-build/MonitorPedidos.Web.dll"
