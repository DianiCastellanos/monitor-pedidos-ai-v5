using Dapper;
using Npgsql;
using MonitorPedidos.Domain.Monitoring;

namespace MonitorPedidos.Infrastructure.Persistence;

/// <summary>
/// Fuente de pedidos para M2 en modo Supabase. Lee public.oc_encabezado vía Npgsql + Dapper.
/// Espejo simétrico de ProductionOrderRepository (SQL Server) — misma abstracción IOrderSource.
/// NO comparte tabla con la simulación U7 (simulated_orders).
/// </summary>
public sealed class SupabaseOrderRepository : IOrderSource
{
    private readonly string _connectionString;

    public SupabaseOrderRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<OrderSnapshot>> GetOrdersInWindowAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<OrderRow>(new CommandDefinition(
            @"SELECT channel_name AS ChannelName, fecha_generacion AS FechaGeneracion
              FROM oc_encabezado
              WHERE fecha_generacion >= @From AND fecha_generacion <= @To",
            new { From = from.UtcDateTime, To = to.UtcDateTime },
            commandTimeout: 30,
            cancellationToken: ct));

        // fecha_generacion es timestamp sin zona (Kind=Unspecified) → se interpreta como UTC,
        // simétrico con la ventana UTC que calcula DbOrderChecker.
        return rows
            .Select(r => new OrderSnapshot(r.ChannelName, new DateTimeOffset(r.FechaGeneracion, TimeSpan.Zero), false))
            .ToList();
    }

    private sealed record OrderRow(string ChannelName, DateTime FechaGeneracion);
}
