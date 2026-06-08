using Microsoft.EntityFrameworkCore;
using MonitorPedidos.Domain.Monitoring;
using MonitorPedidos.Domain.Simulation;
using MonitorPedidos.Infrastructure.Persistence;

namespace MonitorPedidos.Infrastructure.Simulation;

// Implementa IJobStatusSource (leído por JobsChecker) e ISimulatedJobStatusRepository (actualizado por scripts/red-team)
public sealed class SimulatedJobStatusRepository(AppDbContext context) : IJobStatusSource, ISimulatedJobStatusRepository
{
    public async Task<IReadOnlyList<JobStatusSnapshot>> GetCurrentStatusAsync(CancellationToken ct = default)
    {
        var rows = await context.SimulatedJobStatuses.ToListAsync(ct);
        return rows.Select(ToSnapshot).ToList();
    }

    public async Task<IReadOnlyList<SimulatedJobStatus>> GetAllAsync(CancellationToken ct = default)
        => await context.SimulatedJobStatuses.ToListAsync(ct);

    public async Task<SimulatedJobStatus?> GetByJobNameAsync(string jobName, CancellationToken ct = default)
        => await context.SimulatedJobStatuses.FirstOrDefaultAsync(j => j.JobName == jobName, ct);

    public async Task UpdateAsync(SimulatedJobStatus status, CancellationToken ct = default)
    {
        await context.SimulatedJobStatuses
            .Where(j => j.JobName == status.JobName)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status,       status.Status)
                .SetProperty(j => j.LastRunAt,     status.LastRunAt)
                .SetProperty(j => j.ErrorMessage,  status.ErrorMessage), ct);
    }

    public async Task CreateAsync(SimulatedJobStatus status, CancellationToken ct = default)
    {
        context.SimulatedJobStatuses.Add(status);
        await context.SaveChangesAsync(ct);
    }

    // Status="Completed" → job saludable (IsRunning=true, LastSucceeded=true)
    // Status="Failed"/"NotRun" → job fallido (IsRunning=false, LastSucceeded=false)
    private static JobStatusSnapshot ToSnapshot(SimulatedJobStatus j) =>
        new(j.JobName, j.JobName,
            IsRunning:              j.Status is "Completed" or "Running",
            LastExecutedAt:         new DateTimeOffset(j.LastRunAt, TimeSpan.Zero),
            LastExecutionSucceeded: j.Status == "Completed");
}
