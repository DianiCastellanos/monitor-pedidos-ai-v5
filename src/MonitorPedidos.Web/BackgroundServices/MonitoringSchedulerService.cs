using System.Diagnostics;
using System.Text.Json;
using MonitorPedidos.Domain.Monitoring;
using MonitorPedidos.Domain.Rules;
using MonitorPedidos.Domain.Shared;
using MonitorPedidos.Web.Features.ApiChecks;
using MonitorPedidos.Web.Features.Monitoring;
using MonitorPedidos.Web.Services;
using MonitorPedidos.Web.Telemetry;

namespace MonitorPedidos.Web.BackgroundServices;

public sealed class MonitoringSchedulerService : BackgroundService
{
    private static readonly ActivitySource _activity =
        new("MonitorPedidos.Scheduler");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<MonitoringSchedulerService> _logger;
    private readonly MonitorMetrics _metrics;

    public MonitoringSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<MonitoringSchedulerService> logger,
        MonitorMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _config       = config;
        _logger       = logger;
        _metrics      = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitorMin = _config.GetValue("Monitoring:CheckerIntervalMinutes",    5.0);
        var apiMin     = _config.GetValue("Monitoring:ApiCheckerIntervalMinutes", 10.0);
        var brandMin   = _config.GetValue("Monitoring:BrandMonitorPollIntervalMinutes", 3.0);

        _logger.LogInformation(
            "MonitoringSchedulerService started — monitor: {M} min, api: {A} min, brand: {B} min | rid=",
            monitorMin, apiMin, brandMin);

        // Warmup: espera 5s para que el pool de conexiones (Supabase/SQL Server) se inicialice
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var monitorTimer = new PeriodicTimer(TimeSpan.FromMinutes(monitorMin));
        using var apiTimer     = new PeriodicTimer(TimeSpan.FromMinutes(apiMin));

        await Task.WhenAll(
            RunTimerLoopAsync(monitorTimer, IsMonitorChecker, stoppingToken),
            RunTimerLoopAsync(apiTimer,     IsApiChecker,     stoppingToken),
            RunBrandTimerLoopAsync(stoppingToken));
    }

    // Loop dinámico: lee el intervalo desde la regla BrandMonitor en BD.
    // Si la regla no existe o no tiene pollIntervalMinutes, usa appsettings como fallback.
    private async Task RunBrandTimerLoopAsync(CancellationToken stoppingToken)
    {
        await ExecuteCheckersAsync(IsBrandMonitorChecker, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var brandMin = await GetBrandPollIntervalMinutesAsync();
            _logger.LogDebug("[BrandTimer] Próxima consulta a Salesforce en {Min} min", brandMin);

            try { await Task.Delay(TimeSpan.FromMinutes(brandMin), stoppingToken); }
            catch (OperationCanceledException) { break; }

            await ExecuteCheckersAsync(IsBrandMonitorChecker, stoppingToken);
        }
    }

    // Lee pollIntervalMinutes desde la regla activa de BrandMonitor en BD.
    // Fallback: appsettings Monitoring:BrandMonitorPollIntervalMinutes (default 3).
    private async Task<double> GetBrandPollIntervalMinutesAsync()
    {
        var fallback = _config.GetValue("Monitoring:BrandMonitorPollIntervalMinutes", 3.0);
        try
        {
            using var scope  = _scopeFactory.CreateScope();
            var ruleRepo     = scope.ServiceProvider.GetRequiredService<IRuleRepository>();
            var rules        = await ruleRepo.GetActiveByModuleAsync(ModuleId.BrandMonitor);
            if (rules.Count == 0) return fallback;

            using var doc = JsonDocument.Parse(rules[0].ConditionJson);
            if (doc.RootElement.TryGetProperty("pollIntervalMinutes", out var prop)
                && prop.TryGetDouble(out var minutes)
                && minutes >= 1)
                return minutes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[BrandTimer] No se pudo leer intervalo desde regla: {Msg}", ex.Message);
        }
        return fallback;
    }

    private async Task RunTimerLoopAsync(
        PeriodicTimer timer,
        Func<ICheckExecutor, bool> filter,
        CancellationToken stoppingToken)
    {
        // Correr inmediatamente al arrancar — sin esperar el primer tick
        await ExecuteCheckersAsync(filter, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ExecuteCheckersAsync(filter, stoppingToken);
        }
    }

    // Lógica de ejecución extraída: compartida por startup y ticks periódicos
    private async Task ExecuteCheckersAsync(
        Func<ICheckExecutor, bool> filter,
        CancellationToken stoppingToken)
    {
        using var scope   = _scopeFactory.CreateScope();
        var checkers      = scope.ServiceProvider
                                 .GetRequiredService<IEnumerable<ICheckExecutor>>()
                                 .Where(filter);
        var svc           = scope.ServiceProvider.GetRequiredService<IMonitoringService>();
        var timeoutMs     = _config.GetValue("Monitoring:CheckerTimeoutMs", 30_000);

        foreach (var checker in checkers)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(timeoutMs);

            using var span = _activity.StartActivity(
                $"checker.{checker.Module.ToString()}",
                ActivityKind.Internal);
            span?.SetTag("checker.module", checker.Module.ToString());

            var sw = Stopwatch.StartNew();
            try
            {
                await svc.RunCheckAsync(checker, cts.Token);
                sw.Stop();
                span?.SetStatus(ActivityStatusCode.Ok);
                _metrics.RecordCheck(checker.Module.ToString(), "Executed", sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                sw.Stop();
                span?.SetStatus(ActivityStatusCode.Error, "Timeout");
                _metrics.RecordCheck(checker.Module.ToString(), "Timeout", sw.Elapsed.TotalMilliseconds);
                _logger.LogWarning("Checker {Type} timed out after {Ms} ms",
                    checker.GetType().Name, timeoutMs);
            }
            catch (Exception ex)
            {
                sw.Stop();
                span?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _metrics.RecordCheck(checker.Module.ToString(), "Error", sw.Elapsed.TotalMilliseconds);
                _logger.LogError(ex, "Unexpected failure in checker {Type}",
                    checker.GetType().Name);
            }
        }
    }

    private static bool IsMonitorChecker(ICheckExecutor c) =>
        c is DbOrderChecker or DbHealthChecker or JobsChecker;

    private static bool IsApiChecker(ICheckExecutor c) =>
        c is SalesforceApiChecker or MultivendeApiChecker;

    private static bool IsBrandMonitorChecker(ICheckExecutor c) =>
        c is BrandMonitorChecker;
}
