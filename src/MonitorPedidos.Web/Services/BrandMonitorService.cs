using MonitorPedidos.Domain.Dashboard;
using MonitorPedidos.Domain.Monitoring;
using MonitorPedidos.Web.Features.Monitoring;

namespace MonitorPedidos.Web.Services;

public sealed class BrandMonitorService(
    IServiceScopeFactory         scopeFactory,
    ILogger<BrandMonitorService> logger) : IBrandMonitorService
{
    public async Task<IReadOnlyList<BrandSnapshot>> GetCurrentSnapshotsAsync(CancellationToken ct = default)
    {
        // Scope fresco: AppDbContext limpio, independiente del estado del circuito Blazor.
        await using var scope = scopeFactory.CreateAsyncScope();
        var freshRepo         = scope.ServiceProvider.GetRequiredService<IBrandSnapshotRepository>();
        return await freshRepo.GetLatestPerSiteAsync(ct);
    }

    public async Task SimulateAndRefreshAsync(CancellationToken ct = default)
    {
        logger.LogInformation("[BrandMonitor] SimulateAndRefreshAsync — forzando refresh desde Salesforce");
        await using var scope  = scopeFactory.CreateAsyncScope();
        var checker            = scope.ServiceProvider.GetRequiredService<BrandMonitorChecker>();
        // Pasar por IMonitoringService para que LastCheckStore se actualice correctamente.
        // Sin esto el countdown del Dashboard no se resetea al presionar "Consultar Salesforce".
        var monitorService     = scope.ServiceProvider.GetRequiredService<IMonitoringService>();
        await monitorService.RunCheckAsync(checker, ct);
        logger.LogInformation("[BrandMonitor] Refresh completado");
    }

    public async Task<IReadOnlyDictionary<string, int>?> GetLiveCountsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope    = scopeFactory.CreateAsyncScope();
            var sfClient             = scope.ServiceProvider.GetRequiredService<ISalesforceClient>();
            var outcome              = await sfClient.SearchPendingOrdersAsync(ct);
            if (!outcome.IsSuccess) return null;

            return BrandSnapshot.Sites.ToDictionary(
                site => site,
                site => outcome.Items.Count(i =>
                    string.Equals(i.SiteId, site, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            logger.LogWarning("[BrandMonitor] GetLiveCountsAsync falló: {Msg}", ex.Message);
            return null;
        }
    }
}
