# Baseline Estable — Estado Técnico del Sistema (Jun 2026)

> Documento de referencia para el branch `stable-checkpoint-blazor`.
> Cubre todos los cambios aplicados desde el commit `c25e0f4` (HEAD en main)
> hasta el estado local actual. Clasifica cada cambio y define qué mantener y qué revertir.

---

## 1. Causa Raíz del Fallo Principal

### Problema: `StartCircuit` fallaba con "target expects 0 arguments"

```
InvalidDataException: Invocation provides 4 argument(s) but target expects 0.
Parameters to hub method 'StartCircuit' are incorrect.
```

**Causa exacta:** La línea en `Program.cs`:

```csharp
builder.Services.AddSingleton(_ => dbProvider); // expone el provider activo para logging
```

Registraba el valor `string` `"supabase"` como servicio singleton de tipo `string` en el contenedor DI.
Cuando SignalR resolvía los tipos de parámetros del método `ComponentHub.StartCircuit` vía
`IInvocationBinder.GetParameterTypes("StartCircuit")`, la resolución retornaba lista vacía
(método no encontrado), causando el error "target expects 0 arguments".

**Efecto secundario:** Con el circuito roto, los timers no se movían, los botones no respondían
y el auto-refresh estaba inerte — aunque el WebSocket sí conectaba (`/_blazor`).

---

## 2. Inventario de Cambios Clasificados

### A. BUG FIX — Mantener

| # | Archivo | Cambio | Por qué mantener |
|---|---------|--------|-----------------|
| BF-01 | `Program.cs` | Eliminado `AddSingleton(_ => dbProvider)` | **Fix crítico del circuito Blazor** — sin esto el hub no arranca |
| BF-02 | `Program.cs` | `useProdSql = !string.IsNullOrWhiteSpace(prodConn)` | Antes dependía de `DB_PROVIDER == "sqlserver"`, lo que impedía el modo híbrido Supabase+SQL Server |
| BF-03 | `IncidentMaintenanceService.cs` | OCE (`OperationCanceledException`) manejada explícitamente en loop principal | Evita que el servicio logge error en shutdown normal |
| BF-04 | `IncidentMaintenanceService.cs` | `catch (OCE) when (ct.IsCancellationRequested)` separado de `catch (Exception)` | Shutdown graceful; antes ambos iban al mismo handler de error |
| BF-05 | `BrandMonitorService.cs` | `SimulateAndRefreshAsync` ahora pasa por `IMonitoringService.RunCheckAsync` | Sin esto el countdown del Dashboard no se reiniciaba al presionar "Consultar Salesforce" |
| BF-06 | `MonitoringSchedulerService.cs` | Warmup `Task.Delay(5s)` antes de iniciar timers | El pool de conexiones (Supabase/SQL Server) no estaba listo al arrancar; los primeros checks fallaban |
| BF-07 | `Dashboard.razor` | `_brandCountdown` se inicializa con el intervalo real antes del primer check | Antes mostraba `0s` hasta que el checker corría por primera vez |
| BF-08 | `Dashboard.razor` | Countdown decrece hasta 0 y luego reinicia al intervalo (no se queda pegado en 0) | Evita que el timer se congele si el checker se retrasa |

---

### B. CAMBIO ARQUITECTÓNICO — Mantener

| # | Archivo | Cambio | Impacto |
|---|---------|--------|---------|
| CA-01 | `Program.cs` | Cookie: `SameSite` y `SecurePolicy` condicionados a `IsDevelopment()` | Dev: `Lax`/`None` (HTTP local). Prod: `Strict`/`SameAsRequest` (HTTPS). Antes era hardcoded `Lax`/`None` en local |
| CA-02 | `Dashboard.razor` | `LoadBrandPollIntervalAsync()` lee el intervalo de polling desde la tabla `rules` en BD | Hace el intervalo configurable en runtime sin reiniciar la app |
| CA-03 | `Dashboard.razor` | `@inject IRuleRepository RuleRepository` + `@using MonitorPedidos.Domain.Rules` | Necesario para CA-02 |
| CA-04 | `Dashboard.razor` | `CheckersNow` refactorizado: `fast checkers` y `m11` (JobsMonitor) en listas separadas | Evita re-enumerar `IEnumerable<ICheckExecutor>` múltiples veces (era `IEnumerable`, ahora `.ToList()`) |
| CA-05 | `docker-compose.yml` | Renombrado `ecommonitor-*` → `monitor-pedidos-*`; DB `EcomMonitorDb` → `MonitorPedidosDb` | Alineación de nombres con el repo actual |

---

### C. CAMBIO DEBUG — Revertir antes de finalizar baseline

| # | Archivo | Cambio actual (debug) | Debe quedar |
|---|---------|----------------------|-------------|
| CD-01 | `Program.cs` | `opts.EnableDetailedErrors = true` incondicional | `if (builder.Environment.IsDevelopment()) opts.EnableDetailedErrors = true;` |
| CD-02 | `appsettings.Development.json` | `SignalR`, `Http.Connections`, `Components` en nivel `Debug` | Eliminar estas 3 líneas — aumentan ruido en logs; se agregaron para diagnosticar el circuito |
| CD-03 | `Dashboard.razor` | `_userRole = "Técnico"` hardcodeado (auth check comentado) | Restaurar: `var auth = await AuthStateProvider.GetAuthenticationStateAsync();` `_userRole = auth.User.IsInRole("Técnico") ? "Técnico" : "Operador";` |
| CD-04 | `Dashboard.razor` | `startTitleAlert` y `stopTitleAlert` comentados | Restaurar las llamadas JS o eliminarlas limpiamente (no dejar comentadas) |
| CD-05 | Archivos sueltos | `TestCircuit.razor`, `smoke-circuit.spec.ts` (×2), `cookie.txt`, `fxtrace.txt` | No incluir en el baseline — son artefactos de diagnóstico |

---

### E. CAMBIOS POST-BASELINE (sesión Jun 7 2026) — Parte del estado actual

#### E.1 — M2: Independencia total de SQL Server (DB_PROVIDER=supabase)

| # | Archivo | Cambio |
|---|---------|--------|
| CA-06 | `Program.cs` | Routing `IOrderSource` por `DB_PROVIDER`: `supabase` → `SupabaseOrderRepository`; `sqlserver` → `ProductionOrderRepository` (o `SimulatedOrderRepository` como fallback). Nunca toca SQL Server en modo supabase. |
| CA-07 | `SupabaseOrderRepository.cs` *(nuevo)* | Implementación Dapper+Npgsql de `IOrderSource` que lee `oc_encabezado` en Supabase. M2 lee pedidos de Supabase cuando `DB_PROVIDER=supabase`. |
| CA-08 | `OrderSeederService.cs` *(nuevo)* | Hosted service one-shot: siembra `oc_encabezado` solo si está vacía (condicionado a `DB_PROVIDER=supabase` y `OrderSeed:Enabled=true`). Warmup 6s. Filas marcadas `seller='SEED'`. |
| CA-09 | `OrderFeederService.cs` *(nuevo)* | Hosted service opt-in para simulación M2: inserta pedidos periódicos en `oc_encabezado` modo **Burst** — lote + silencio de `BurstIntervalMinutes`. Si `BurstIntervalMinutes > WindowMinutes` de la regla M2, el monitor transita OK→Critical→OK naturalmente. Filas marcadas `seller='SIM'`. |
| CA-10 | `appsettings.json` / `appsettings.Development.json` | Nuevas secciones `OrderSeed` y `OrderFeeder`. Dev: `OrderFeeder.Enabled=true`, `BurstIntervalMinutes=15`. Prod: `Enabled=false`. |

**Tabla Supabase creada:** `public.oc_encabezado` (id_aut_order, id_order, seller, channel_name, creation_date, fecha_generacion, estado_factura, estado_actual_orden) + índice en `(fecha_generacion, channel_name)`.

**Separación de responsabilidades:**

| Servicio | Tabla | Propósito |
|----------|-------|-----------|
| `OrdersSimulatorService` | `simulated_orders` | Simulación U7 — no toca M2 |
| `OrderSeederService` | `oc_encabezado` | Carga inicial única (solo si vacía) |
| `OrderFeederService` | `oc_encabezado` | Simulación periódica M2 opt-in |

#### E.2 — UI: Ajustes de texto Brand Monitor

| # | Archivo | Texto anterior | Texto actual |
|---|---------|---------------|--------------|
| TX-01 | `BrandMonitorTable.razor` L20 | `pendientes totales` | `pedidos por descargar` |
| TX-02 | `Dashboard.razor` L192 | `— pendientes en Salesforce por marca` | `— Pedidos pendientes de descarga en Salesforce por marca` |

*Solo cambios de texto. Sin modificaciones de lógica, estilos, servicios ni estructura.*

---

### D. PENDIENTE DE DECISIÓN — No incluir en baseline todavía

| # | Archivo | Item | Motivo |
|---|---------|------|--------|
| PD-01 | `.github/workflows/docker-publish.yml` | Cambios en pipeline CI | Requiere validar en entorno Docker antes de incluir en baseline |
| PD-02 | `docker-entrypoint.sh` | Cambios de startup Docker | Idem |
| PD-03 | `OrderSyncService.cs` | Refactorización de queries | No revisado en detalle en esta sesión |

---

## 3. Arquitectura Actual del Sistema

```
┌─────────────────────────────────────────────────────────────┐
│  MonitorPedidos.Web  (Blazor Server, .NET 8)               │
│                                                             │
│  ┌──────────────┐   WebSocket /_blazor                     │
│  │  Dashboard   │ ◄─────────────────── Browser             │
│  │  .razor      │                                          │
│  │  Timer 30s   │   SignalR Hub (ComponentHub)             │
│  │  Timer  1s   │   + AlertsHub (/hubs/alerts)             │
│  └──────────────┘                                          │
│                                                             │
│  Background Services:                                       │
│  ├── MonitoringSchedulerService  (PeriodicTimer)           │
│  │     warmup 5s → checks cada N min                       │
│  ├── IncidentMaintenanceService  (Task.Delay 1h)           │
│  ├── OrderSyncService            (Task.Delay 30s)          │
│  └── OrdersSimulatorService      (PeriodicTimer)           │
│                                                             │
│  DI (Scoped por request / circuito Blazor):                │
│  ├── AppDbContext         ← DB_PROVIDER (supabase|sqlserver)│
│  ├── IMonitoringService                                     │
│  ├── IIncidentService                                       │
│  ├── IBrandMonitorService                                   │
│  └── IRuleRepository                                        │
│                                                             │
│  DI (Singleton):                                            │
│  ├── AlertBroadcaster    ← events entre circuito y hub      │
│  ├── LastCheckStore      ← caché de último resultado check  │
│  ├── SalesforceTokenCache                                   │
│  └── INotificationService                                   │
└─────────────────────────────────────────────────────────────┘
         │                           │
         ▼                           ▼
  Supabase (PostgreSQL)        SQL Server 172.16.0.41
  AppDbContext (default)       ProductionDb (read-only)
  SupabaseConnection           vtainternet_qa
```

**Modo activo local:** `DB_PROVIDER=supabase` en `.env`
**Modo ProductionDb:** se activa si `ConnectionStrings__ProductionDb` está configurado,
independientemente del `DB_PROVIDER` (modo híbrido — BF-02).

---

## 4. Decisiones Clave

### DEC-01: Eliminar `AddSingleton(_ => dbProvider)`
Registrar un `string` como servicio DI singleton es un anti-patrón que rompe la resolución
de métodos del hub SignalR. Si se necesita exponer el proveedor activo para logging, usar
`ILogger` con structured logging en lugar de inyectar el string.

### DEC-02: `ASPNETCORE_ENVIRONMENT=Development` en `.env`
Sin esta variable, `dotnet run --no-launch-profile` arranca en modo Production, lo que:
- No sirve static web assets del framework (CSS MIME type vacío)
- Deshabilita `EnableDetailedErrors`
- Aplica políticas de cookie más estrictas
Esta variable debe permanecer en `.env` (gitignored) para desarrollo local.

### DEC-03: Cookie policy condicional por entorno
`Lax`/`None` es necesario en HTTP local (desarrollo). `Strict`/`SameAsRequest` es necesario
en producción HTTPS. La condición `IsDevelopment()` resuelve ambos casos sin duplicar config.

### DEC-04: `useProdSql` basado en presencia de `ProductionDb`, no en `DB_PROVIDER`
Permite modo híbrido: Supabase como BD principal + SQL Server como fuente de pedidos reales
(read-only). Antes, si `DB_PROVIDER=supabase`, nunca se activaba `ProductionOrderRepository`.

---

## 5. Riesgos Mitigados

| Riesgo | Mitigación aplicada |
|--------|-------------------|
| Circuito Blazor roto en startup | BF-01: eliminado `AddSingleton(string)` |
| App arrancando en modo Production en dev local | DEC-02: `ASPNETCORE_ENVIRONMENT=Development` en `.env` |
| Background services crasheando en shutdown | BF-03, BF-04: OCE manejada explícitamente |
| Countdown del Dashboard congelado en 0 | BF-07, BF-08: inicialización y lógica de decremento corregidas |
| "Consultar Salesforce" no reiniciaba countdown | BF-05: ruta correcta a través de `IMonitoringService` |
| Primeros checks fallando por pool no listo | BF-06: warmup 5s en MonitoringSchedulerService |

---

## 6. Riesgos Conocidos Restantes (Abiertos)

| ID | Riesgo | Severidad | Descripción |
|----|--------|-----------|-------------|
| R-01 | Timer sin límite de reconexión | Media | Si el circuito Blazor se rompe en runtime (timeout de red, restart), los timers del Dashboard quedan activos sin intentar reconectar la UI. El usuario debe recargar la página. |
| R-02 | DbContext en Background Services | Media | `MonitoringSchedulerService` y `OrderSyncService` crean scope (`IServiceScopeFactory`) para cada ciclo, pero si el ciclo tarda más que el siguiente, puede haber solapamiento de operaciones sobre la misma BD. |
| R-03 | `_userRole` hardcodeado (CD-03) | Alta | `Dashboard.razor` tiene `_userRole = "Técnico"` hardcodeado — bypasea la verificación de rol real. **Debe revertirse antes de que el baseline sea definitivo.** |
| R-04 | `catch (Exception)` genérico en checkers | Baja | Algunos checkers tienen `catch (Exception ex) { _logger.LogError(ex, "error"); }` sin diferenciar tipo de fallo. Dificulta triage de incidentes. |
| R-05 | CSS MIME type en Production mode | Baja | Si `ASPNETCORE_ENVIRONMENT` no está en `.env`, la app arranca en Production y el CSS no se sirve correctamente. Depende de que `.env` esté presente. |
| R-06 | SQL Server 172.16.0.41 inaccesible en red local dev | Baja | `ConnectionStrings__SyncSourceDb` y `ProductionDb` apuntan a 172.16.0.41 — si la red VPN/LAN no está disponible, `OrderSyncService` y `ProductionOrderRepository` fallan silenciosamente (timeout). Comportamiento esperado pero debe ser observable. |
| R-07 | Logs SignalR en Debug (CD-02) | Baja | Si se deja `appsettings.Development.json` con `SignalR: Debug`, los logs se saturan en desarrollo. No es funcional pero afecta observabilidad. |

---

## 7. Checklist para Finalizar el Baseline

Antes de hacer commit al branch `stable-checkpoint-blazor`:

- [ ] **CD-01** — Revertir `EnableDetailedErrors` a condicional en `Program.cs`
- [ ] **CD-02** — Eliminar logging Debug de SignalR/Http.Connections/Components en `appsettings.Development.json`
- [ ] **CD-03** — Restaurar verificación real de rol en `Dashboard.razor` (quitar `_userRole = "Técnico"`)
- [ ] **CD-04** — Limpiar llamadas JS de title alert (restaurar o eliminar limpiamente)
- [ ] **CD-05** — Excluir del commit: `TestCircuit.razor`, `smoke-circuit.spec.ts`, `cookie.txt`, `fxtrace.txt`
- [ ] Ejecutar `dotnet build` sin errores
- [ ] Ejecutar Playwright smoke test: `1 passed` con circuito activo
- [ ] Confirmar que dashboard carga, timers se mueven, botón "Chequear ahora" responde

---

## 8. Estado del Repo

| Branch | HEAD | Estado |
|--------|------|--------|
| `main` | `c25e0f4` | 29 commits ahead of origin/main. Cambios debug sin commitear en working tree. |
| `stable-checkpoint-blazor` | `c25e0f4` | Creado localmente. Sin commits propios todavía. Pendiente aplicar checklist §7. |
| `origin/main` (GitHub) | 29 commits atrás | **No tocar hasta que el usuario valide el baseline local.** |

> `.env` es gitignored — nunca se commitea. Contiene `ASPNETCORE_ENVIRONMENT=Development`
> y `DB_PROVIDER=supabase`. Ambos son requisitos para que la app arranque correctamente en local.
