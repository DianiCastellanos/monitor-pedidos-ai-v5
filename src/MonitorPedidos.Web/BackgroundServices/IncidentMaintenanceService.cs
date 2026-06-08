using MonitorPedidos.Domain.Dashboard;
using MonitorPedidos.Domain.Incidents;

namespace MonitorPedidos.Web.BackgroundServices;

public sealed class IncidentMaintenanceService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration       _config;
    private readonly ILogger<IncidentMaintenanceService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public IncidentMaintenanceService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<IncidentMaintenanceService> logger)
    {
        _scopeFactory = scopeFactory;
        _config       = config;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunIncidentPurgeAsync(stoppingToken);
            await RunSnapshotPurgeAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunIncidentPurgeAsync(CancellationToken ct)
    {
        var retentionDays = _config.GetValue<int>("Incidents:RetentionDays", 90);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo    = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();
            var deleted = await repo.PurgeExpiredAsync(retentionDays, ct);
            _logger.LogInformation(
                "Incident purge completed. RetentionDays={Days} Deleted={Count}",
                retentionDays, deleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — no loggear como error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Incident purge failed. RetentionDays={Days}", retentionDays);
        }
    }

    // IT3 Paso 7 — retención de snapshots Brand Monitor (configurable, default 48h)
    private async Task RunSnapshotPurgeAsync(CancellationToken ct)
    {
        var retentionHours = _config.GetValue<int>("BrandMonitor:SnapshotRetentionHours", 48);
        var cutoff         = DateTime.UtcNow.AddHours(-retentionHours);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBrandSnapshotRepository>();
            await repo.DeleteOlderThanAsync(cutoff, ct);
            _logger.LogInformation(
                "Brand snapshot purge completed. RetentionHours={Hours} Cutoff={Cutoff:u}",
                retentionHours, cutoff);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — no loggear como error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Brand snapshot purge failed. RetentionHours={Hours}", retentionHours);
        }
    }
}
