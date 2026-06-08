using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using MonitorPedidos.Web.Telemetry;
using MonitorPedidos.Domain.Incidents;
using MonitorPedidos.Domain.Monitoring;
using MonitorPedidos.Domain.Notifications;
using MonitorPedidos.Web.Features.ApiChecks;
using MonitorPedidos.Infrastructure.Incidents;
using MonitorPedidos.Infrastructure.JobMonitoring;
using MonitorPedidos.Infrastructure.Logging;
// NullNotificationService reemplazado por NotificationService en U6
using MonitorPedidos.Infrastructure.Persistence;
using MonitorPedidos.Web.BackgroundServices;
using MonitorPedidos.Web.Components;
using MonitorPedidos.Web.Features.Monitoring;
using MonitorPedidos.Web.Middleware;
using MonitorPedidos.Web.Services;
using MonitorPedidos.Infrastructure.Rules;
using MonitorPedidos.Domain.Rules;
using MonitorPedidos.Domain.Dashboard;
using MonitorPedidos.Infrastructure.Dashboard;
using MonitorPedidos.Web.Hubs;
using MonitorPedidos.Domain.Simulation;
using MonitorPedidos.Infrastructure.Simulation;
using MonitorPedidos.Web.Telemetry;

// Busca .env desde el directorio actual hacia arriba (raíz del repo)
static string? FindEnvFile()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (int i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, ".env");
        if (File.Exists(path)) return path;
    }
    return null;
}

var envFile = FindEnvFile();
if (envFile is not null)
{
    foreach (var line in File.ReadAllLines(envFile))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
        var idx = trimmed.IndexOf('=');
        if (idx > 0)
            Environment.SetEnvironmentVariable(trimmed[..idx].Trim(), trimmed[(idx + 1)..].Trim());
    }
}

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.AddSerilogLogging();

// EF Core — proveedor controlado por DB_PROVIDER (sqlserver | supabase)
var dbProvider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "sqlserver";
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (dbProvider.Equals("supabase", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(builder.Configuration.GetConnectionString("SupabaseConnection"));
    else
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});
// OpenTelemetry — Traces + Metrics (logging via Serilog existente)
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
        .AddEntityFrameworkCoreInstrumentation()
        .AddSqlClientInstrumentation()
        .AddSource("MonitorPedidos.*")
        .AddConsoleExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("MonitorPedidos.Checkers")
        .AddConsoleExporter());
builder.Services.AddSingleton<MonitorMetrics>();

// Data Protection — persiste claves entre reinicios
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")))
    .SetApplicationName("MonitorPedidos");

// Cookie authentication — sin ASP.NET Core Identity
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath         = "/Identity/Select";
        options.AccessDeniedPath  = "/Identity/AccessDenied";
        options.ExpireTimeSpan    = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly   = true;
        options.Cookie.SameSite     = builder.Environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.None : CookieSecurePolicy.SameAsRequest;
    });

// Authorization — deny-by-default (RF-26, BR-AUTHZ-01)
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Razor Pages (Identity: Select, Logout, AccessDenied)
builder.Services.AddRazorPages();

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── U6: SignalR + Real-Time ───────────────────────────────────────────────
builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = true;
});
builder.Services.AddSingleton<AlertBroadcaster>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddSingleton<LastCheckStore>();
builder.Services.AddScoped<IBrandSnapshotRepository, BrandSnapshotRepository>();
builder.Services.AddScoped<BrandMonitorChecker>();
builder.Services.AddScoped<ICheckExecutor>(sp => sp.GetRequiredService<BrandMonitorChecker>());
builder.Services.AddScoped<IBrandMonitorService, BrandMonitorService>();
builder.Services.AddScoped<ITechnicalLogReader, TechnicalLogReader>();
// ─────────────────────────────────────────────────────────────────────────

// ── U2: Persistence & Incidents ──────────────────────────────────────────
builder.Services.AddScoped<IIncidentRepository, IncidentRepository>();
builder.Services.AddScoped<IIncidentService, IncidentService>();
builder.Services.AddHostedService<IncidentMaintenanceService>();
builder.Services.AddHostedService<OrderSyncService>();     // Sync oc_encabezado vtainternet_qa → MonitorPedidosDb
// ─────────────────────────────────────────────────────────────────────────

// ── U3: Detection & Classification ───────────────────────────────────────
// ADR-U3-02: IEnumerable<ICheckExecutor> resuelve los 3 automáticamente
builder.Services.AddScoped<ICheckExecutor, DbOrderChecker>();
builder.Services.AddScoped<ICheckExecutor, DbHealthChecker>();
builder.Services.AddScoped<ICheckExecutor, JobsChecker>();
builder.Services.AddScoped<IMonitoringService, MonitoringService>();
builder.Services.AddHostedService<MonitoringSchedulerService>();
// ─────────────────────────────────────────────────────────────────────────

// ── U7: Simulation & Red-Teaming ─────────────────────────────────────────
builder.Services.Configure<SimulationOptions>(
    builder.Configuration.GetSection(SimulationOptions.Section));

// M2 — IOrderSource: la fuente de pedidos depende exclusivamente de DB_PROVIDER.
//   supabase  → SupabaseOrderRepository (lee oc_encabezado en Supabase). NUNCA consulta SQL Server.
//   sqlserver → ProductionOrderRepository (oc_encabezado en SQL Server) si hay ProductionDb;
//               si no, SimulatedOrderRepository como fallback (comportamiento previo intacto).
// La tabla de simulación U7 (simulated_orders) queda exclusiva para simulación, no para M2.
if (dbProvider.Equals("supabase", StringComparison.OrdinalIgnoreCase))
{
    var supabaseConn = builder.Configuration.GetConnectionString("SupabaseConnection");
    builder.Services.AddScoped<IOrderSource>(_ => new SupabaseOrderRepository(supabaseConn!));
}
else
{
    var prodConn = builder.Configuration.GetConnectionString("ProductionDb");
    if (!string.IsNullOrWhiteSpace(prodConn))
        builder.Services.AddScoped<IOrderSource>(_ => new ProductionOrderRepository(prodConn!));
    else
        builder.Services.AddScoped<IOrderSource, SimulatedOrderRepository>();
}

// Seeder de carga inicial única para oc_encabezado (se auto-desactiva fuera de modo supabase)
builder.Services.AddHostedService<OrderSeederService>();
// Simulación opcional M2: inserta pedidos recientes periódicamente (solo si OrderFeeder:Enabled=true)
builder.Services.AddHostedService<OrderFeederService>();

builder.Services.AddScoped<ISimulatedOrderRepository,     SimulatedOrderRepository>();
// JobsMonitor — real desde Task Scheduler, con fallback a simulación
builder.Services.Configure<JobsMonitorOptions>(
    builder.Configuration.GetSection(JobsMonitorOptions.Section));

if (!string.IsNullOrEmpty(builder.Configuration.GetSection(JobsMonitorOptions.Section)["Server"]))
    builder.Services.AddScoped<IJobStatusSource, SchtasksJobStatusSource>();
else
    builder.Services.AddScoped<IJobStatusSource, SimulatedJobStatusRepository>();

builder.Services.AddScoped<ISimulatedJobStatusRepository, SimulatedJobStatusRepository>();
builder.Services.AddHostedService<OrdersSimulatorService>();
// ─────────────────────────────────────────────────────────────────────────

// ── U5: Rules Management ──────────────────────────────────────────────────
builder.Services.AddScoped<IRuleRepository, RuleRepository>();
builder.Services.AddScoped<IRuleHistoryRepository, RuleHistoryRepository>();
builder.Services.AddScoped<IRuleManagementService, RuleManagementService>();
// ─────────────────────────────────────────────────────────────────────────

// ── U4: External Integrations ─────────────────────────────────────────────
builder.Services.AddScoped<ICheckExecutor, SalesforceApiChecker>();
builder.Services.AddScoped<ICheckExecutor, MultivendeApiChecker>();

// Salesforce OAuth2 — singleton para caché de token + handler para inyección automática
builder.Services.AddSingleton<SalesforceTokenCache>();
builder.Services.AddTransient<SalesforceAuthHandler>();

// HttpClient dedicado para el endpoint de token OAuth2 (host raíz, sin site path)
builder.Services.AddHttpClient("SalesforceAuth", c => { c.Timeout = TimeSpan.FromSeconds(10); });

// ADR-U4-02: Typed HTTP clients con política Polly compartida por instancia de request
builder.Services
    .AddHttpClient<ISalesforceClient, SalesforceClient>(c =>
    {
        // Sin BaseAddress — SalesforceClient construye URLs absolutas por site (Salesforce:Sites[])
        c.Timeout = TimeSpan.FromSeconds(15);
        // Bearer token inyectado por SalesforceAuthHandler
    })
    .AddHttpMessageHandler<SalesforceAuthHandler>()
    .AddPolicyHandler((services, _) =>
        ApiRetryPolicy.Create(services.GetRequiredService<ILoggerFactory>().CreateLogger("ApiRetryPolicy")));

builder.Services
    .AddHttpClient<IMultivendeClient, MultivendeClient>(c =>
    {
        var baseUrl = builder.Configuration["Multivende:BaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl))
            c.BaseAddress = new Uri(baseUrl);
        c.Timeout = TimeSpan.FromSeconds(10);
        var apiKey = builder.Configuration["Multivende:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
            c.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    })
    .AddPolicyHandler((services, _) =>
        ApiRetryPolicy.Create(services.GetRequiredService<ILoggerFactory>().CreateLogger("ApiRetryPolicy")));
// ─────────────────────────────────────────────────────────────────────────

// Exception handler (NFR §3)
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Pipeline de seguridad (orden crítico — nfr-design-patterns.md §1)
// ForwardedHeaders: Render termina TLS en proxy y reenvía HTTP al container.
// KnownNetworks/KnownProxies vacíos = acepta cualquier proxy (requerido en Render/cloud).
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseExceptionHandler();
app.UseAntiforgery();

app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHub<AlertsHub>("/hubs/alerts");

// M11 Jobs — endpoint push: SR-SDEV02CO reporta estado cada 5 min (schtasks no disponible en Render/Linux)
app.MapPost("/api/jobs/report", async (
    HttpContext httpCtx,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    JobReportRequest req) =>
{
    var expectedKey = config["JobsReport:ApiKey"];
    if (string.IsNullOrEmpty(expectedKey))
        return Results.StatusCode(503); // desactivado si no hay clave configurada

    if (!httpCtx.Request.Headers.TryGetValue("X-Jobs-Report-Key", out var h) || h.ToString() != expectedKey)
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.JobName))
        return Results.BadRequest("jobName requerido");

    var isHealthy = req.Status is "Ready" or "Running" or "Completed";

    using var scope = scopeFactory.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<ISimulatedJobStatusRepository>();
    var job  = await repo.GetByJobNameAsync(req.JobName);

    if (job is null)
    {
        var seed    = SimulatedJobStatus.Create(req.JobName);
        var created = isHealthy ? seed.MarkCompleted()
                                : seed.MarkFailed(req.ErrorMessage ?? $"Job en estado: {req.Status}");
        await repo.CreateAsync(created);
    }
    else
    {
        var updated = isHealthy ? job.MarkCompleted()
                                : job.MarkFailed(req.ErrorMessage ?? $"Job en estado: {req.Status}");
        await repo.UpdateAsync(updated);
    }

    return Results.Ok(new { updated = true, jobName = req.JobName, status = req.Status });
}).AllowAnonymous();

// Migraciones automáticas — solo SQL Server (Supabase ya tiene el esquema aplicado via MCP)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (!dbProvider.Equals("supabase", StringComparison.OrdinalIgnoreCase))
        db.Database.Migrate();
}

app.Run();

// Requerido para WebApplicationFactory en tests de integración
public partial class Program { }

// DTO para POST /api/jobs/report
record JobReportRequest(string JobName, string Status, string? ErrorMessage = null);
