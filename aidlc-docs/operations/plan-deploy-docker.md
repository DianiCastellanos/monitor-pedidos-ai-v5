# Plan de Despliegue — Docker
## MonitorPedidos AI

**Fecha**: 2026-06-02  
**Responsable**: Diana Castellanos  
**Estado**: 📋 PLANIFICADO — pendiente ejecución

---

## Antes de empezar — lo que necesitas instalar

| Herramienta | Para qué | Dónde descargar |
|---|---|---|
| **Docker Desktop** | Motor de contenedores | https://www.docker.com/products/docker-desktop |
| **Docker Compose** | Orquestar múltiples contenedores | Viene incluido con Docker Desktop |

> **Requisito mínimo**: 4 GB RAM para Docker + SQL Server corriendo simultáneo.  
> Instalar Docker Desktop, iniciar sesión, verificar con `docker --version` en PowerShell.

---

## Arquitectura Docker

```
┌─────────────────────────────────────────┐
│  docker-compose                         │
│                                         │
│  ┌──────────────────┐                   │
│  │  monitorpedidos  │ :5000 → :80       │
│  │  (app .NET 8)    │                   │
│  └──────────────────┘                   │
│                                         │
└─────────────────────────────────────────┘
        │                │                │
        ▼                ▼                ▼
  172.16.0.41       192.168.20.91   account.demandware.com
  MonitorPedidosDb  vtainternet_qa  (Salesforce OCAPI)
  (AppDb R/W)       (ProductionDb   
                     READ-ONLY M2)
```

---

## ⚠️ Limitación importante — M11 Jobs

El checker de M11 usa `schtasks.exe` que es un comando **exclusivo de Windows**.  
Un contenedor Docker estándar corre Linux → `schtasks` NO funciona.

**Consecuencia**: M11 siempre mostrará `"Sin conexión a SR-SDEV02CO (timeout)"` desde Docker.

**Opciones para resolverlo** (ordenadas de menor a mayor esfuerzo):

| Opción | Descripción | Complejidad |
|---|---|---|
| A — Aceptarlo | M11 siempre Critical desde Docker — solo M2/M3/M4/Brand Monitor operan | Ninguna |
| B — Windows Container | Usar imagen base `mcr.microsoft.com/windows/servercore` | Alta |
| C — API en el servidor | Crear un endpoint REST en SR-SDEV02CO que devuelva el estado del job | Media |

**Recomendación**: opción A para el primer deploy, opción C cuando sea necesario.

---

## Archivos a crear (paso a paso)

### Paso 1 — Dockerfile

Crear el archivo `Dockerfile` en la raíz del proyecto:

```dockerfile
# Etapa 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar archivos de proyecto y restaurar dependencias
COPY ["src/MonitorPedidos.Web/MonitorPedidos.Web.csproj",          "src/MonitorPedidos.Web/"]
COPY ["src/MonitorPedidos.Domain/MonitorPedidos.Domain.csproj",     "src/MonitorPedidos.Domain/"]
COPY ["src/MonitorPedidos.Infrastructure/MonitorPedidos.Infrastructure.csproj", "src/MonitorPedidos.Infrastructure/"]
RUN dotnet restore "src/MonitorPedidos.Web/MonitorPedidos.Web.csproj"

# Copiar el resto del código y publicar
COPY . .
RUN dotnet publish "src/MonitorPedidos.Web/MonitorPedidos.Web.csproj" \
    -c Release -o /app/publish --no-restore

# Etapa 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Copiar solo el resultado del build
COPY --from=build /app/publish .

# Crear carpetas necesarias en runtime
RUN mkdir -p /app/logs /app/keys

EXPOSE 80
ENTRYPOINT ["dotnet", "MonitorPedidos.Web.dll"]
```

---

### Paso 2 — docker-compose.yml

Crear `docker-compose.yml` en la raíz del proyecto:

```yaml
version: '3.8'

services:

  # La aplicación MonitorPedidos AI
  monitorpedidos:
    build:
      context: .
      dockerfile: Dockerfile
    container_name: monitorpedidos-app
    ports:
      - "5000:80"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:80
    env_file:
      - .env.docker          # credenciales (nunca al repo)
    depends_on:
      sqlserver-app:
        condition: service_healthy
    volumes:
      - app-logs:/app/logs    # logs persistentes
      - app-keys:/app/keys    # claves de Data Protection
    restart: unless-stopped
    networks:
      - monitor-network

  # SQL Server para la BD de la aplicación (incidentes, reglas, snapshots)
  sqlserver-app:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: monitorpedidos-sqlserver
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=<SA_PASSWORD>
    ports:
      - "1434:1433"           # 1434 externo para no pisar SQL Server local
    volumes:
      - sql-data:/var/opt/mssql
    healthcheck:
      test: ["CMD", "/opt/mssql-tools/bin/sqlcmd", "-S", "localhost",
             "-U", "sa", "-P", "<SA_PASSWORD>", "-Q", "SELECT 1"]
      interval: 10s
      timeout: 5s
      retries: 10
    restart: unless-stopped
    networks:
      - monitor-network

volumes:
  sql-data:
  app-logs:
  app-keys:

networks:
  monitor-network:
    driver: bridge
```

---

### Paso 3 — .env.docker

Crear `.env.docker` en la raíz (también gitignored — agregar a `.gitignore`):

```bash
# BD de la aplicación — apunta al contenedor SQL Server
# App DB — MonitorPedidosDb en servidor interno (escritura)
ConnectionStrings__DefaultConnection=Server=tcp:172.16.0.41,1433;Database=MonitorPedidosDb;User Id=<DB_USER>;Password=<DB_PASSWORD>;TrustServerCertificate=True;Encrypt=True;Connect Timeout=10;ConnectRetryCount=0

# BD de producción — READ-ONLY — apunta al servidor real de la empresa
ConnectionStrings__ProductionDb=Server=tcp:<IP_SERVIDOR_BD>,1433;Database=<NOMBRE_BD_PRODUCCION>;User Id=<DB_USER>;Password=<DB_PASSWORD>;TrustServerCertificate=True;Encrypt=True;Connect Timeout=5;ConnectRetryCount=0

# Salesforce OCAPI
Salesforce__ClientId=<CLIENT_ID_REAL>
Salesforce__ClientPassword=<CLIENT_PASSWORD_REAL>
Salesforce__Sites__0__Host=www.patprimo.com
Salesforce__Sites__0__SiteId=PatPrimo
Salesforce__Sites__1__Host=www.sevenseven.com
Salesforce__Sites__1__SiteId=SevenSeven
Salesforce__Sites__2__Host=www.ostu.com
Salesforce__Sites__2__SiteId=Ostu
Salesforce__Sites__3__Host=www.atmosmovement.com
Salesforce__Sites__3__SiteId=Atmos
Salesforce__OAuthTokenUrl=https://account.demandware.com/dwsso/oauth2/access_token
Salesforce__OAuthGrantType=client_credentials
```

> **Llenar con los valores reales** de tu `.env` actual antes de usar.

---

### Paso 4 — Agregar .env.docker al .gitignore

En `.gitignore` agregar:
```
.env.docker
```

---

## Ejecución — paso a paso

### Primera vez

```powershell
# 1. Ir a la raíz del proyecto
cd C:\Users\dcastellanos\Estacion4\PRD_AI_DLC\MonitorPedidos

# 2. Construir la imagen
docker compose build

# 3. Levantar todo (SQL Server + App)
docker compose up -d

# 4. Ver que los contenedores están corriendo
docker compose ps

# 5. Ver logs de la app
docker compose logs monitorpedidos -f

# 6. Verificar que la app responde
curl http://localhost:5000/Identity/Select
```

### Aplicar migraciones EF Core (solo la primera vez)

```powershell
# Ejecutar migraciones dentro del contenedor corriendo
docker compose exec monitorpedidos dotnet ef database update \
  --project src/MonitorPedidos.Web/MonitorPedidos.Web.csproj

# Alternativa: correr el contenedor con el comando de migración
docker run --rm \
  --network monitorpedidos_monitor-network \
  --env-file .env.docker \
  monitorpedidos-app \
  dotnet ef database update
```

---

## Comandos del día a día

| Acción | Comando |
|---|---|
| Levantar app | `docker compose up -d` |
| Detener app | `docker compose down` |
| Ver logs en vivo | `docker compose logs -f monitorpedidos` |
| Reiniciar solo la app | `docker compose restart monitorpedidos` |
| Reconstruir tras cambios de código | `docker compose up -d --build` |
| Ver estado de contenedores | `docker compose ps` |
| Entrar al contenedor | `docker compose exec monitorpedidos bash` |
| Ver uso de recursos | `docker stats` |

---

## Acceso desde la red interna

Para que otras personas de la empresa accedan (sin internet):

1. Verificar IP de la máquina donde corre Docker:
   ```powershell
   ipconfig | findstr "IPv4"
   ```
2. Abrir el puerto 5000 en el firewall de Windows:
   ```powershell
   netsh advfirewall firewall add rule name="MonitorPedidos" dir=in action=allow protocol=TCP localport=5000
   ```
3. Acceder desde otro equipo de la red: `http://<IP-DE-TU-PC>:5000`

---

## Verificación final

Después de levantar, verificar que todo funciona:

- [ ] `http://localhost:5000` responde con la pantalla de selección de identidad
- [ ] Login como Analista Operativo funciona
- [ ] Dashboard muestra los módulos M2/M3/M4/M11
- [ ] Brand Monitor muestra los 4 sites
- [ ] M4 muestra latencia (BD App conectada)
- [ ] M3 muestra Salesforce y Multivende
- [ ] Al cerrar y reabrir, los incidentes persisten

---

## Resumen de archivos a crear

| Archivo | Ubicación | Ir al repo |
|---|---|---|
| `Dockerfile` | Raíz del proyecto | ✅ Sí |
| `docker-compose.yml` | Raíz del proyecto | ✅ Sí |
| `.env.docker` | Raíz del proyecto | ❌ **Nunca** — gitignored |

---

## Notas finales

- El contenedor de **SQL Server tarda ~30 segundos** en estar listo la primera vez.
- Los **logs** se guardan en un volumen persistente — no se pierden al reiniciar.
- Las **claves de Data Protection** también son persistentes — evita que las cookies expiren al reiniciar.
- **M11** reportará `Sin conexión` desde Linux — es esperado y correcto.
- Para producción real considerar HTTPS con un reverse proxy (nginx o Traefik).
