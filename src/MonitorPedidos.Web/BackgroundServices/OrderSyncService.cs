using Dapper;
using Microsoft.Data.SqlClient;

namespace MonitorPedidos.Web.BackgroundServices;

/// <summary>
/// Sincroniza órdenes desde vtainternet_qa (ProductionDb) hacia MonitorPedidosDb.
/// Permite que M2 y otros módulos lean de la misma BD de la app sin depender de vtainternet_qa.
/// </summary>
public sealed class OrderSyncService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<OrderSyncService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public OrderSyncService(IConfiguration config, ILogger<OrderSyncService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // El sync se activa solo cuando ambas conexiones son SQL Server.
        // No depende de DB_PROVIDER: permite modo híbrido (supabase + sqlserver).
        _logger.LogInformation("OrderSyncService iniciado — sincroniza oc_encabezado cada {Min} min", Interval.TotalMinutes);

        // Warmup: espera 5s antes del primer sync para que el pool de conexiones esté listo
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Primera sincronización al arrancar
        await RunSyncAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            await RunSyncAsync(stoppingToken);
        }
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        // SyncSourceDb = vtainternet_qa (fuente original)
        // DefaultConnection = MonitorPedidosDb (destino — misma BD de la app)
        var sourceCs = _config.GetConnectionString("SyncSourceDb");
        var targetCs = _config.GetConnectionString("DefaultConnection");

        // Requiere ambas conexiones SQL Server configuradas.
        // Si alguna está vacía o apunta a PostgreSQL/Supabase, se desactiva.
        static bool IsSqlServer(string? cs) =>
            !string.IsNullOrWhiteSpace(cs) &&
            !cs.Contains("Host=",        StringComparison.OrdinalIgnoreCase) &&
            !cs.Contains("supabase.com", StringComparison.OrdinalIgnoreCase);

        if (!IsSqlServer(sourceCs) || !IsSqlServer(targetCs))
        {
            _logger.LogInformation("[OrderSync] Desactivado — SyncSourceDb o DefaultConnection no son SQL Server");
            return;
        }

        try
        {
            // Ventana de sincronización configurable (default 30 días para capturar historial)
            var windowDays = _config.GetValue("OrderSync:WindowDays", 30);
            var from       = DateTime.Now.AddDays(-windowDays);

            // 1. Leer desde vtainternet_qa — solo columnas necesarias para M2 y Brand Monitor
            IEnumerable<OrderRow> sourceRows;
            await using (var sourceConn = new SqlConnection(sourceCs))
            {
                sourceRows = await sourceConn.QueryAsync<OrderRow>(
                    @"SELECT IdAutOrder, IdOrder, Seller, ChannelName,
                             CreationDate, FechaGeneracion,
                             EstadoFactura, EstadoActualOrden
                      FROM oc_encabezado WITH (NOLOCK)
                      WHERE FechaGeneracion >= @From
                      ORDER BY IdAutOrder ASC",
                    new { From = from },
                    commandTimeout: 60);
            }

            var rows = sourceRows.AsList();
            if (rows.Count == 0)
            {
                _logger.LogInformation("[OrderSync] Sin registros en los últimos {D} días — nada que sincronizar", windowDays);
                return;
            }

            // 2. MERGE en MonitorPedidosDb — PK = IdAutOrder (auto-incremental de vtainternet_qa)
            await using var targetConn = new SqlConnection(targetCs);
            await targetConn.OpenAsync(ct);

            const string mergeSql = @"
                MERGE [dbo].[oc_encabezado] AS target
                USING (SELECT @IdAutOrder, @IdOrder, @Seller, @ChannelName,
                              @CreationDate, @FechaGeneracion,
                              @EstadoFactura, @EstadoActualOrden)
                      AS source (IdAutOrder, IdOrder, Seller, ChannelName,
                                 CreationDate, FechaGeneracion,
                                 EstadoFactura, EstadoActualOrden)
                ON target.IdAutOrder = source.IdAutOrder
                WHEN MATCHED THEN
                    UPDATE SET IdOrder           = source.IdOrder,
                               Seller            = source.Seller,
                               ChannelName       = source.ChannelName,
                               CreationDate      = source.CreationDate,
                               FechaGeneracion   = source.FechaGeneracion,
                               EstadoFactura     = source.EstadoFactura,
                               EstadoActualOrden = source.EstadoActualOrden
                WHEN NOT MATCHED THEN
                    INSERT (IdAutOrder, IdOrder, Seller, ChannelName,
                            CreationDate, FechaGeneracion,
                            EstadoFactura, EstadoActualOrden)
                    VALUES (source.IdAutOrder, source.IdOrder, source.Seller,
                            source.ChannelName, source.CreationDate,
                            source.FechaGeneracion, source.EstadoFactura,
                            source.EstadoActualOrden);";

            var affected = await targetConn.ExecuteAsync(mergeSql, rows, commandTimeout: 120);

            _logger.LogInformation(
                "[OrderSync] ✓ {Count} registros sincronizados vtainternet_qa → MonitorPedidosDb (ventana={D} días)",
                affected, windowDays);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // TCP connection timeout en frío — no propagar, el próximo ciclo reintentará
            _logger.LogWarning("[OrderSync] Timeout de conexión en arranque frío — reintentará en {Min} min", Interval.TotalMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Si vtainternet_qa no está disponible (sin VPN) el resto de la app sigue funcionando
            _logger.LogWarning(
                "[OrderSync] No se pudo sincronizar — vtainternet_qa inaccesible: {Msg}",
                ex.Message[..Math.Min(100, ex.Message.Length)]);
        }
    }

    // Clase con setters para compatibilidad con Dapper
    // EstadoActualOrden es INT en vtainternet_qa (no varchar)
    private sealed class OrderRow
    {
        public decimal   IdAutOrder        { get; set; }   // PK numérico auto-incremental
        public string?   IdOrder           { get; set; }
        public string?   Seller            { get; set; }
        public string?   ChannelName       { get; set; }
        public DateTime? CreationDate      { get; set; }
        public DateTime? FechaGeneracion   { get; set; }
        public string?   EstadoFactura     { get; set; }
        public int?      EstadoActualOrden { get; set; }   // INT en origen
    }
}
