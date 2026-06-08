namespace MonitorPedidos.Domain.Simulation;

public interface ISimulatedJobStatusRepository
{
    Task<IReadOnlyList<SimulatedJobStatus>> GetAllAsync(CancellationToken ct = default);
    Task<SimulatedJobStatus?> GetByJobNameAsync(string jobName, CancellationToken ct = default);
    Task UpdateAsync(SimulatedJobStatus status, CancellationToken ct = default);
    Task CreateAsync(SimulatedJobStatus status, CancellationToken ct = default);
}
