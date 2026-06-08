# Plan de Integración OpenTelemetry — MonitorPedidos AI

**Fecha**: 2026-06-08  
**Estado**: PLANIFICADO — pendiente ejecución  
**Stack**: Blazor Server .NET 8 + EF Core + SQL Server + Supabase  

---

## Objetivo

Agregar observabilidad de producción a MonitorPedidos AI mediante OpenTelemetry, capturando trazas, métricas y logs correlacionados sin modificar la lógica de negocio existente.

---

## Qué se tendrá al final

```
App MonitorPedidos
  │
  ├─ Traces  ── cada request HTTP, cada query EF Core, cada llamada Salesforce/Multivende
  ├─ Metrics ── conteo de ejecuciones por checker, latencia, incidentes activos
  └─ Logs    ── logs estructurados con TraceId visible (correlacionados con sus trazas)
```

El destino del exporter se decide en el **Paso 5** — los primeros pasos usan console exporter y no requieren infraestructura externa.

---

## Inventario de señales por módulo

| Módulo | Signal | Qué se captura |
|--------|--------|----------------|
| HTTP requests (ASP.NET Core) | Trace | Método, ruta, status code, duración |
| EF Core (AppDbContext) | Trace | Query SQL, tabla, duración |
| Dapper (ProductionDb) | Trace | Query SQL via SqlClient instrumentation |
| HttpClient (Salesforce, Multivende) | Trace | URL, método, status, duración |
| MonitoringSchedulerService | Trace custom | Módulo, status (OK/Warning/Critical), detalle |
| Checkers (M2–M12) | Metric custom | Ejecuciones por módulo y estado, duración ms |
| Incidentes | Metric custom | Gauge de incidentes activos en tiempo real |
| Runtime .NET | Metric auto | GC, thread pool, memory pressure |

---

## Paso 1 — Paquetes NuGet

**Archivo**: `src/MonitorPedidos.Infrastructure/MonitorPedidos.Infrastructure.csproj`  
(los de instrumentación van en Infrastructure; los de hosting en Web)

```xml
<!-- Base OTel -->
<PackageReference Include="OpenTelemetry" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.9.*" />

<!-- Instrumentación automática -->
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.SqlClient" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.9.*" />

<!-- Exporters -->
<PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.9.*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.9.*" />
```

**Verificación**: `dotnet build` sin errores tras agregar los paquetes.

---

## Paso 2 — Configuración en `Program.cs`

**Archivo**: `src/MonitorPedidos.Web/Program.cs`  
Agregar después del bloque de `AddDbContext`, antes de `builder.Build()`:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService("MonitorPedidos", serviceVersion: "1.0.0")
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName
        }))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.RecordException = true)
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation(o => o.SetDbStatementForText = true)
        .AddSqlClientInstrumentation()
        .AddSource("MonitorPedidos.*")
        .AddConsoleExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("MonitorPedidos.Checkers")
        .AddConsoleExporter())
    .WithLogging(l => l
        .AddConsoleExporter());
```

**Verificación**: Al levantar la app aparecen trazas en consola para cada request HTTP y query EF Core.

---

## Paso 3 — Métricas propias de los checkers

**Archivo nuevo**: `src/MonitorPedidos.Web/Telemetry/MonitorMetrics.cs`

```csharp
public sealed class MonitorMetrics
{
    private readonly Counter<int>    _checkRuns;
    private readonly Histogram<double> _checkDuration;

    public MonitorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("MonitorPedidos.Checkers");

        _checkRuns = meter.CreateCounter<int>(
            "monitor.checker.runs",
            description: "Ejecuciones de cada checker por módulo y estado");

        _checkDuration = meter.CreateHistogram<double>(
            "monitor.checker.duration_ms",
            unit: "ms",
            description: "Duración de cada ejecución de checker");
    }

    public void RecordCheck(string module, string status, double durationMs)
    {
        _checkRuns.Add(1, new TagList
        {
            { "module", module },
            { "status", status }   // OK | Warning | Critical
        });
        _checkDuration.Record(durationMs, new TagList { { "module", module } });
    }
}
```

Registrar en `Program.cs`:
```csharp
builder.Services.AddSingleton<MonitorMetrics>();
```

**Verificación**: Las métricas `monitor.checker.runs` y `monitor.checker.duration_ms` aparecen en consola.

---

## Paso 4 — Trazas en `MonitoringSchedulerService`

**Archivo**: `src/MonitorPedidos.Web/BackgroundServices/MonitoringSchedulerService.cs`

Agregar el `ActivitySource` como campo estático e instrumentar el loop de checkers:

```csharp
// Campo estático en la clase
private static readonly ActivitySource _activity =
    new("MonitorPedidos.Scheduler");

// Dentro del loop donde se ejecuta cada checker:
using var span = _activity.StartActivity(
    $"checker.run.{checker.ModuleId}",
    ActivityKind.Internal);

span?.SetTag("checker.module", checker.ModuleId);
span?.SetTag("checker.interval_s", intervalSeconds);

var sw = Stopwatch.StartNew();
var result = await checker.CheckAsync(ct);
sw.Stop();

span?.SetTag("checker.status", result.Status.ToString());
span?.SetTag("checker.details", result.Details);

if (result.Status == CheckStatus.Critical)
    span?.SetStatus(ActivityStatusCode.Error, result.Details);

_metrics.RecordCheck(checker.ModuleId, result.Status.ToString(), sw.Elapsed.TotalMilliseconds);
```

**Verificación**: Cada ciclo del scheduler genera una traza con sus tags de módulo y estado.

---

## Paso 5 — Cambiar exporter al destino final

Cuando se elija el destino (Grafana Cloud, Seq, Azure Monitor, etc.), se reemplaza `AddConsoleExporter()` por `AddOtlpExporter()` en el bloque de `Program.cs`.

```csharp
// Reemplazar en los tres bloques (Tracing, Metrics, Logging):
.AddOtlpExporter(o =>
{
    o.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"]!);
    o.Headers  = $"Authorization=Basic {builder.Configuration["Otel:Token"]}";
    o.Protocol = OtlpExportProtocol.HttpProtobuf;
})
```

Variables a agregar en `.env` (nunca hardcodeadas):
```
Otel__Endpoint=https://<backend-otlp-url>
Otel__Token=<token-del-backend>
```

### Opciones de backend

| Backend | Costo | Tipo | Notas |
|---------|-------|------|-------|
| Grafana Cloud | Gratis (tier básico) | Cloud | Traces + Metrics + Logs unificados |
| Seq | Gratis (self-hosted) | Local/Red interna | Excelente para logs estructurados y debug |
| Azure Monitor | Pago | Cloud | Integración nativa con Azure |
| Console | Gratis | Dev only | Sin infraestructura — para validar la instrumentación |

---

## Orden de ejecución

| Paso | Tarea | Riesgo | Verificación |
|------|-------|--------|--------------|
| 1 | Agregar paquetes NuGet | Ninguno | `dotnet build` 0 errores |
| 2 | Configurar `Program.cs` con console exporter | Ninguno | Trazas visibles en consola al arrancar |
| 3 | Crear `MonitorMetrics` + registrar en DI | Ninguno | Métricas de checkers en consola |
| 4 | Instrumentar `MonitoringSchedulerService` | Bajo | Trazas por ciclo de checker visibles |
| 5 | Elegir backend + configurar OTLP exporter | Bajo | Datos visibles en el backend elegido |

Cada paso es independiente y reversible — si algo falla, se revierte sin afectar la lógica de negocio.

---

## Áreas a NO tocar

- `ProductionDb` (vtainternet_qa) — la instrumentación de SqlClient solo añade observabilidad de lectura, no modifica queries
- `AppDbContext` — EF Core instrumentation es no-invasiva (interceptors internos)
- Lógica de checkers (M2–M12) — solo se agrega la llamada `_metrics.RecordCheck(...)` al final de cada ejecución
- `.env` — el token del backend OTLP va aquí, nunca en código

---

## Archivos a crear/modificar

| Acción | Archivo |
|--------|---------|
| Modificar | `src/MonitorPedidos.Infrastructure/MonitorPedidos.Infrastructure.csproj` |
| Modificar | `src/MonitorPedidos.Web/Program.cs` |
| Crear | `src/MonitorPedidos.Web/Telemetry/MonitorMetrics.cs` |
| Modificar | `src/MonitorPedidos.Web/BackgroundServices/MonitoringSchedulerService.cs` |
| Modificar | `.env` (agregar `Otel__Endpoint` y `Otel__Token` cuando se elija backend) |

---

*Versión 1.0 — Plan elaborado 2026-06-08 — Pendiente ejecución*
