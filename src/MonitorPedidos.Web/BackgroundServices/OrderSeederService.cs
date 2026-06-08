using Dapper;
using Npgsql;

namespace MonitorPedidos.Web.BackgroundServices;

/// <summary>
/// Carga inicial ÚNICA de pedidos de prueba en public.oc_encabezado (solo modo Supabase).
/// Siembra únicamente si la tabla está vacía — en reinicios posteriores no re-rellena.
/// No es un keep-alive: tras la carga inicial, el estado de M2 depende solo de los
/// registros y fechas reales en oc_encabezado. Si no llegan pedidos nuevos, las filas
/// envejecen fuera de la ventana y M2 pasa a Warning/Critical según las reglas.
/// </summary>
public sealed class OrderSeederService : BackgroundService
{
    private static readonly string[] Channels = ["SALESFORCE", "MULTIVENDE"];

    private readonly IConfiguration _config;
    private readonly ILogger<OrderSeederService> _logger;

    public OrderSeederService(IConfiguration config, ILogger<OrderSeederService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var provider = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "sqlserver";
        if (!provider.Equals("supabase", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[OrderSeeder] Omitido — DB_PROVIDER no es supabase");
            return;
        }

        if (!_config.GetValue("OrderSeed:Enabled", true))
        {
            _logger.LogInformation("[OrderSeeder] Omitido — OrderSeed:Enabled=false");
            return;
        }

        var connStr = _config.GetConnectionString("SupabaseConnection");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            _logger.LogWarning("[OrderSeeder] Omitido — SupabaseConnection vacía");
            return;
        }

        // Warmup: deja que el pool de conexiones se inicialice antes del primer acceso
        try { await Task.Delay(TimeSpan.FromSeconds(6), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(stoppingToken);

            var existing = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM oc_encabezado", cancellationToken: stoppingToken));

            if (existing > 0)
            {
                _logger.LogInformation(
                    "[OrderSeeder] oc_encabezado ya tiene {Count} filas — carga inicial omitida", existing);
                return;
            }

            var perChannel = _config.GetValue("OrderSeed:OrdersPerChannel", 3);
            var now        = DateTime.UtcNow;

            var rows = new List<object>(Channels.Length * perChannel);
            foreach (var ch in Channels)
            {
                for (var i = 0; i < perChannel; i++)
                {
                    // Distribuye en los últimos ~9 min: 1, 4, 7… min atrás
                    var minutesAgo = 1 + i * 3;
                    var ts         = now.AddMinutes(-minutesAgo);
                    rows.Add(new
                    {
                        IdOrder           = $"SEED-{ch}-{i + 1}",
                        Seller            = "SEED",
                        ChannelName       = ch,
                        CreationDate      = ts,
                        FechaGeneracion   = ts,
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

            var inserted = await conn.ExecuteAsync(new CommandDefinition(insertSql, rows, cancellationToken: stoppingToken));

            _logger.LogInformation(
                "[OrderSeeder] ✓ Carga inicial única: {Count} pedidos sembrados en oc_encabezado (canales: {Channels})",
                inserted, string.Join(", ", Channels));
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning("[OrderSeeder] No se pudo sembrar oc_encabezado: {Msg}", ex.Message);
        }
    }
}
