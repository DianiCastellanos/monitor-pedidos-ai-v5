# Docker Desktop — Guía paso a paso (Monitor Pedidos eCommerce)

## Visión general
Este documento explica cómo levantar **Monitor Pedidos eCommerce** en Docker Desktop en tu máquina local, conectando a **Supabase** como base de datos (sin necesidad de SQL Server local).

**Tiempo estimado:** 10-15 minutos la primera vez (build + run).

---

## Paso 1: Verificar que Docker Desktop esté corriendo

Abre **Docker Desktop** desde Windows:
- Busca "Docker Desktop" en el menú Inicio y haz clic en él
- Espera a que el icono del demonio ✓ aparezca en la bandeja del sistema

Verifica desde PowerShell:
```powershell
docker --version
docker info
```

Si ves errores como "Cannot connect to Docker daemon", reinicia Docker Desktop.

---

## Paso 2: Preparar el archivo `deploy.env`

El contenedor Docker necesita un archivo `.env` con credenciales (como el que usas localmente, pero sin passwords hardcodeados en el repositorio).

1. **Abre PowerShell** en la raíz del proyecto:
   ```powershell
   cd C:\Users\dcastellanos\Estacion4\PRD_AI_DLC\MonitorPedidos
   ```

2. **Crea el archivo `deploy.env`** (está en `.gitignore`, nunca se sube):
   ```powershell
   New-Item -Path "deploy.env" -ItemType File -Force
   ```

3. **Abre `deploy.env` en tu editor** (VS Code, Notepad++, etc.) y copia lo siguiente:
   ```env
   # ══════════════════════════════════════════════════════
   # Monitor Pedidos eCommerce — Docker Desktop (Supabase)
   # ══════════════════════════════════════════════════════

   # Switch BD — en Docker Desktop usamos Supabase
   DB_PROVIDER=supabase

   # Supabase — PostgreSQL cloud (proyecto: uzieandeucdqolerscgw)
   # Pooler IPv4 (requerido en redes sin IPv6)
   ConnectionStrings__SupabaseConnection=Host=aws-1-us-east-1.pooler.supabase.com;Port=5432;Database=postgres;Username=<SUPABASE_USER>;Password=<SUPABASE_PASSWORD>;SSL Mode=Require

   # Salesforce OCAPI — OAuth2
   Salesforce__ClientId=<SALESFORCE_CLIENT_ID>
   Salesforce__ClientPassword=<SALESFORCE_CLIENT_PASSWORD>
   Salesforce__OAuthTokenUrl=https://account.demandware.com/dwsso/oauth2/access_token
   Salesforce__OAuthGrantType=client_credentials

   # Sites Colombia — rutas de las tiendas
   Salesforce__Sites__0__Host=www.patprimo.com
   Salesforce__Sites__0__SiteId=PatPrimo
   Salesforce__Sites__1__Host=www.sevenseven.com
   Salesforce__Sites__1__SiteId=SevenSeven
   Salesforce__Sites__2__Host=www.ostu.com
   Salesforce__Sites__2__SiteId=Ostu
   Salesforce__Sites__3__Host=www.atmosmovement.com
   Salesforce__Sites__3__SiteId=Atmos
   ```

4. **Guarda el archivo** (Ctrl+S).

> **Nota:** `deploy.env` está en `.gitignore` — nunca lo subas a GitHub. Este archivo contiene credenciales locales.

---

## Paso 3: Construir la imagen Docker

Abre PowerShell en la raíz del proyecto:

```powershell
docker build -t ecommonitor-app:latest .
```

**Esto tardará 5-8 minutos la primera vez** porque:
- Descarga `mcr.microsoft.com/dotnet/sdk:8.0` (~700 MB)
- Restaura dependencias NuGet
- Publica la app en modo Release
- Instala `mssql-tools18` (para sqlcmd)

**Salida esperada:**
```
[+] Building 8.5s (14/14) FINISHED
 => => writing image sha256:abc123def456...
 => => naming to docker.io/library/ecommonitor-app:latest
```

Si ves errores, verifica:
- Docker Desktop esté corriendo
- Tengas conexión a internet (para descargar imágenes base)
- El archivo `Dockerfile` esté en la raíz (`C:\...\MonitorPedidos\Dockerfile`)

---

## Paso 4: Levantar el contenedor

Ejecuta en PowerShell:

```powershell
docker run -d `
  --name monitor-pedidos `
  -p 8080:8080 `
  --env-file deploy.env `
  monitor-pedidos-ecommerce:latest
```

**Qué hace este comando:**
| Flag | Significado |
|------|---|
| `-d` | Modo daemon (corre en background) |
| `--name monitor-pedidos` | Nombre del contenedor |
| `-p 8080:8080` | Mapea puerto 8080 local → 8080 del contenedor |
| `--env-file deploy.env` | Lee variables de `deploy.env` |
| `monitor-pedidos-ecommerce:latest` | Imagen a ejecutar |

**Salida esperada:**
```
abc123def4567890abcd1234567890 (hash del contenedor)
```

---

## Paso 5: Verificar que está corriendo

### 5a. Ver contenedores activos
```powershell
docker ps
```

Deberías ver algo como:
```
CONTAINER ID   IMAGE                            NAMES             PORTS
abc123def456   monitor-pedidos-ecommerce:latest  monitor-pedidos   0.0.0.0:8080->8080/tcp
```

### 5b. Ver logs
```powershell
docker logs monitor-pedidos --tail 30
```

**Logs esperados (últimas líneas):**
```
[Monitor Pedidos eCommerce] DB_PROVIDER=supabase — omitiendo SQL Server, iniciando app directamente...
[Monitor Pedidos eCommerce] Iniciando app...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://[::]:8080
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

Si ves errores de conexión a Supabase, verifica que `deploy.env` tenga las credenciales correctas.

---

## Paso 6: Acceder a la app

Abre tu navegador y ve a:
```
http://localhost:8080
```

Deberías ver el **Dashboard de Monitor Pedidos eCommerce** con las tarjetas de cada módulo (Brand Monitor, DbOrderChecker, etc.).

---

## Paso 7: Ver logs en tiempo real

Para ver qué está pasando en el contenedor mientras interactúas con la app:

```powershell
docker logs monitor-pedidos -f
```

(Presiona `Ctrl+C` para salir)

---

## Comandos del día a día

### Detener el contenedor
```powershell
docker stop monitor-pedidos
```

La app sigue "ahí" pero no está corriendo. Puedes:
- **Reiniciarla:** `docker start monitor-pedidos`
- **Eliminarla:** `docker rm monitor-pedidos` (necesitarías hacer `docker run` de nuevo)

### Reiniciar
```powershell
docker restart monitor-pedidos
```

### Ver estado
```powershell
docker ps -a  # Todos (incluyendo parados)
docker stats monitor-pedidos  # CPU, memoria en vivo
```

### Limpiar recursos
```powershell
# Eliminar solo el contenedor parado
docker rm monitor-pedidos

# Eliminar la imagen (si quieres rebuild)
docker rmi monitor-pedidos-ecommerce:latest

# Eliminar volumenes, redes, etc. no usados
docker system prune
```

---

## Reconstruir tras cambios de código

Si hiciste cambios en C# o archivos de la app:

1. **Detén el contenedor:**
   ```powershell
   docker stop monitor-pedidos
   ```

2. **Elimínalo:**
   ```powershell
   docker rm monitor-pedidos
   ```

3. **Rebuild:**
   ```powershell
   docker build -t monitor-pedidos-ecommerce:latest .
   ```

4. **Levanta de nuevo:**
   ```powershell
   docker run -d `
     --name monitor-pedidos `
     -p 8080:8080 `
     --env-file deploy.env `
     monitor-pedidos-ecommerce:latest
   ```

**Shortcut — Script PowerShell reutilizable:**

Crea un archivo `rebuild.ps1` en la raíz del proyecto:

```powershell
# rebuild.ps1 — Reconstruye y levanta Monitor Pedidos eCommerce

docker stop monitor-pedidos -ErrorAction SilentlyContinue
docker rm monitor-pedidos -ErrorAction SilentlyContinue
docker build -t monitor-pedidos-ecommerce:latest .
docker run -d `
  --name monitor-pedidos `
  -p 8080:8080 `
  --env-file deploy.env `
  monitor-pedidos-ecommerce:latest

Write-Host "✓ Monitor Pedidos eCommerce levantado — http://localhost:8080"
```

Ejecuta así:
```powershell
.\rebuild.ps1
```

---

## Opcional: docker-compose (incluye SQL Server local)

Si quieres levantar SQL Server local también (modo desarrollo con BD interna):

```powershell
docker compose up -d
```

Esto levanta **dos servicios:**
- `sqlserver` (SQL Server Express en puerto 1433)
- `app` (Monitor Pedidos eCommerce en puerto 8080)

El archivo `docker-compose.yml` en la raíz lo configura automáticamente.

**Para detener:**
```powershell
docker compose down
```

---

## Troubleshooting

### Error: "Cannot connect to Docker daemon"
- Asegúrate que Docker Desktop esté abierto y corriendo
- En Windows, verifica que Hyper-V esté habilitado: Settings → Apps → Turns Windows features on or off → marca Hyper-V

### Error: "Port 8080 is already allocated"
Algo más está usando el puerto 8080. Opciones:
- **Cambiar puerto:** usa `-p 8081:8080` en `docker run`
- **Ver qué está usando 8080:**
  ```powershell
  netstat -ano | findstr ":8080"
  ```

### Error: "Image not found"
Reconstruye:
```powershell
docker build -t ecommonitor-app:latest .
```

### Error de conexión a Supabase en los logs
Verifica credenciales en `deploy.env`:
```powershell
# Ver variables del contenedor
docker inspect ecommonitor | findstr "Env"
```

Si `ConnectionStrings__SupabaseConnection` no aparece o está mal formada, actualiza `deploy.env` y rebuild.

### La app levanta pero "BD no disponible"
Esto puede ser normal si:
- Supabase pooler temporal no está disponible → espera 30s, recarga el navegador
- La VPN está desconectada (Supabase está en internet)

Verifica conexión:
```powershell
ping aws-1-us-east-1.pooler.supabase.com
```

---

## Resumen visual

```
┌─────────────────────────────────────┐
│  Docker Desktop en Windows          │
│  (Task Manager → corre en background)
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  Dockerfile                         │
│  (especifica cómo construir imagen) │
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  docker build                       │
│  (crea: monitor-pedidos-ecommerce)  │
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  docker run                         │
│  (levanta: monitor-pedidos)         │
│  Puerto: 8080 → http://localhost... │
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  deploy.env (credenciales)          │
│  Supabase, Salesforce, Sites        │
└─────────────────────────────────────┘
              ↓
┌─────────────────────────────────────┐
│  docker logs                        │
│  Verifica que app está escuchando   │
└─────────────────────────────────────┘
              ↓
      http://localhost:8080
  ✓ Dashboard de Monitor Pedidos
      eCommerce
```

---

## Preguntas frecuentes

**P: ¿Tengo que hacer `docker build` cada vez que levanto la app?**
R: No. `docker build` solo cuando cambies código. `docker run` crea un contenedor de la imagen existente. Si ya corriste `docker build` y luego cambias la app, simplemente haz `rebuild.ps1` (que re-build + relanza).

**P: ¿Puedo editar código sin rebuild?**
R: No, porque el contenedor corre bytecode compilado. Tendrías que entrar dentro del contenedor (`docker exec`) y hacer `dotnet run`, pero es complicado. Más fácil: rebuild (~30s en cambios pequeños, ~5min en rebuild limpio).

**P: ¿Dónde se guardan los datos?**
R: En Supabase (cloud). Si quieres persistencia local, usa `docker compose` con volumen SQL Server.

**P: ¿Puedo cambiar el puerto?**
R: Sí, en `docker run`: `-p 8081:8080` para escuchar en 8081 localmente. Pero el contenedor siempre escucha en 8080 internamente.

**P: ¿Cómo debug dentro del contenedor?**
R: Ver logs en vivo: `docker logs ecommonitor -f`. Si necesitas entrar: `docker exec -it ecommonitor bash`.

---

## Próximos pasos

1. ✓ Levanta la app en Docker Desktop
2. ✓ Abre http://localhost:8080 y verifica que funciona
3. **Opcional:** Configura CI/CD para pushear imagen a GitHub Container Registry (ghcr.io)
4. **Opcional:** Despliega a producción (Render, Azure Container Instances, etc.)

---

**Última actualización:** 2026-06-05
**Versión:** 1.1 (Actualizado: nombre de la app → Monitor Pedidos eCommerce)
