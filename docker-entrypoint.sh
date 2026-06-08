#!/bin/bash
set -e

DB_PROVIDER="${DB_PROVIDER:-sqlserver}"

# Render asigna PORT en runtime (default 10000).
# ASPNETCORE_URLS debe usar ese puerto para que el health check funcione.
export ASPNETCORE_URLS="http://+:${PORT:-10000}"

echo "[MonitorPedidos] Puerto: ${PORT:-10000} | DB_PROVIDER: $DB_PROVIDER"

# En Supabase: arrancar directamente, sin esperar SQL Server.
# SQL Server es inaccesible desde Render (red interna / VPN).
if [ "$(echo "$DB_PROVIDER" | tr '[:upper:]' '[:lower:]')" = "supabase" ]; then
    echo "[MonitorPedidos] Modo Supabase — iniciando app..."
    exec dotnet MonitorPedidos.Web.dll
fi

# En SQL Server: esperar BD con reintentos (solo entorno local con docker-compose).
# Requiere mssql-tools18 instalado en la imagen (no incluido en modo Render/Supabase).
echo "[MonitorPedidos] Modo SQL Server — esperando BD (máx. 10 intentos)..."
ATTEMPTS=0
MAX_ATTEMPTS=10
until sqlcmd -S "$DB_HOST,1433" -U "$DB_USER" -P "$DB_PASSWORD" -C -Q "SELECT 1" > /dev/null 2>&1; do
    ATTEMPTS=$((ATTEMPTS + 1))
    if [ "$ATTEMPTS" -ge "$MAX_ATTEMPTS" ]; then
        echo "[MonitorPedidos] SQL Server no respondió — iniciando en modo degradado."
        break
    fi
    echo "[MonitorPedidos] Esperando SQL Server (intento $ATTEMPTS/$MAX_ATTEMPTS)..."
    sleep 3
done

echo "[MonitorPedidos] Iniciando app..."
exec dotnet MonitorPedidos.Web.dll
