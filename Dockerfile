# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restaurar dependencias primero (cache layer)
COPY src/MonitorPedidos.Domain/MonitorPedidos.Domain.csproj           src/MonitorPedidos.Domain/
COPY src/MonitorPedidos.Infrastructure/MonitorPedidos.Infrastructure.csproj src/MonitorPedidos.Infrastructure/
COPY src/MonitorPedidos.Web/MonitorPedidos.Web.csproj                 src/MonitorPedidos.Web/

RUN dotnet restore src/MonitorPedidos.Web/MonitorPedidos.Web.csproj

COPY src/ src/

RUN dotnet publish src/MonitorPedidos.Web/MonitorPedidos.Web.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV TZ=America/Bogota
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

COPY --from=build /app/publish .
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# Puerto por defecto 10000 (Render). Render sobreescribe PORT en runtime.
# ASPNETCORE_URLS se asigna dinámicamente en docker-entrypoint.sh usando $PORT.
EXPOSE 10000
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["/docker-entrypoint.sh"]
