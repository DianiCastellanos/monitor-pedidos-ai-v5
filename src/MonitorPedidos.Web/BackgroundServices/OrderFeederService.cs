using Dapper;
using Npgsql;

namespace MonitorPedidos.Web.BackgroundServices;

/// <summary>
/// Simulación de ciclo de vida de pedidos para M2 — solo DB_PROVIDER=supabase, solo si OrderFeeder:Enabled=true.
///
/// Opera en modo Burst: inserta un lote de pedidos y luego espera BurstIntervalMinutes (silencio).
/// Si BurstIntervalMinutes > WindowMinutes de la regla M2, los pedidos del lote expiran durante
/// el silencio → M2 atraviesa las transiciones OK → Critical → OK de forma natural.
///
/// Para observar transiciones:
///   BurstIntervalMinutes DEBE ser mayor que el WindowMinutes configurado en la regla M2.
///   Ejemplo: regla con Window=10 min + BurstIntervalMinutes=15 → ~5 min de Critical por ciclo.
///
/// Distinto de:
///   · OrdersSimulatorService → simulación U7 sobre simulated_orders (no toca oc_encabezado).
///   · OrderSeederService      → carga inicial ÚNICA (no es periódico).
///
/// Filas marcadas seller='SIM'. Se limpian automáticamente por RetentionHours.
/// Desactivar con OrderFeeder:Enabled=false cuando exista ingestión real.
/// </summary>
public sealed class OrderFeederService : BackgroundService
{
    private static readonly string[] Channels = ["SALESFORCE", "MULTIVENDE"];

    private readonly IConfiguration _config;
    private readonly ILogger<OrderFeederService> _logger;

    public OrderFeederService(IConfiguration config, ILogger<OrderFeederService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var provider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "sqlserver";
        if (!provider.Equals("supabase", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[OrderFeeder] Omitido — DB_PROVIDER no es supabase");
            return;
        }

        if (!_config.GetValue("OrderFeeder:Enabled", false))
        {
            _logger.LogInformation("[OrderFeeder] Desactivado (OrderFeeder:Enabled=false)");
            return;
        }

        var connStr = _config.GetConnectionString("SupabaseConnection");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogWarning("[OrderFeeder] Omitido — SupabaseConnection vacía");
            return;
        }

        // BurstIntervalMinutes reemplaza IntervalMinutes; fallback para retrocompatibilidad.
        var burstInterval  = _config.GetValue("OrderFeeder:BurstIntervalMinutes",
                                 _config.GetValue("OrderFeeder:IntervalMinutes", 15.0));
        var perChannel     = _config.GetValue("OrderFeeder:OrdersPerChannel", 2);
        var retentionHours = _config.GetValue("OrderFeeder:RetentionHours", 1);

        _logger.LogInformation(
            "[OrderFeeder] Simulación M2 activa — {N} pedido(s)/canal, silencio entre bursts: {Min} min " +
            "(retención {H}h). Los pedidos expiran en {Window} min aprox → se esperan transiciones OK→Critical→OK " +
            "si BurstIntervalMinutes > WindowMinutes de la regla M2.",
            perChannel, burstInterval, retentionHours, burstInterval);

        // Warmup: evitar competir con el seeder inicial
        try { await Task.Delay(TimeSpan.FromSeconds(7), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Primer burst inmediato al arrancar
        await InsertBurstAsync(connStr, perChannel, retentionHours, burstInterval, stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(burstInterval));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await InsertBurstAsync(connStr, perChannel, retentionHours, burstInterval, stoppingToken);
        }
        catch (OperationCanceledException) { /* shutdown limpio */ }
    }

    private async Task InsertBurstAsync(
        string connStr, int perChannel, int retentionHours, double burstIntervalMin, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var now  = DateTime.UtcNow;
            var rows = new List<object>(Channels.Length * perChannel);
            foreach (var ch in Channels)
            {
                for (var i = 0; i < perChannel; i++)
                {
                    rows.Add(new
                    {
                        IdOrder           = $"SIM-{ch}-{now:yyyyMMddHHmmss}-{i + 1}",
                        Seller            = "SIM",
                        ChannelName       = ch,
                        CreationDate      = now,
                        FechaGeneracion   = now,
                        EstadoFactura     = "OK",
                        EstadoActualOrden = 1
                    });
                }
            }

            const string insertSql = @"
                INSERT INTO oc_encabezado
                    (id_order, seller, channel_name, creation_date, fecha_generacion, estado_factura, estado_actual_orden)
                VALUES
                    (@IdOrder, @Seller, @ChannelName, @CreationDate, @FechaGeneracion, @EstadoFactura, @EstadoActualOrden)";

            var inserted = await conn.ExecuteAsync(new CommandDefinition(insertSql, rows, cancellationToken: ct));

            // Solo limpia filas SIM antiguas; nunca toca filas reales ni SEED
            var cutoff  = now.AddHours(-retentionHours);
            var deleted = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM oc_encabezado WHERE seller = 'SIM' AND fecha_generacion < @Cutoff",
                new { Cutoff = cutoff }, cancellationToken: ct));

            _logger.LogInformation(
                "[OrderFeeder] Burst completado: +{Ins} pedidos | -{Del} expirados | " +
                "próximo burst en {Min} min — M2 irá a Critical en ~{Window} min (si regla tiene ventana < {Min} min)",
                inserted, deleted, burstIntervalMin, burstIntervalMin, burstIntervalMin);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning("[OrderFeeder] Error en burst: {Msg}", ex.Message);
        }
    }
}
