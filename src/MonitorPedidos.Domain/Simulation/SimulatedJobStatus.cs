namespace MonitorPedidos.Domain.Simulation;

public sealed class SimulatedJobStatus
{
    public int     Id           { get; private set; }
    public string  JobName      { get; private set; } = null!;  // "SalesforceDownload" | "MultivendeDownload"
    public DateTime LastRunAt   { get; private set; }
    public string  Status       { get; private set; } = null!;  // "Running" | "Completed" | "Failed" | "NotRun"
    public string? ErrorMessage { get; private set; }

    private SimulatedJobStatus() { }

    public static SimulatedJobStatus Create(string jobName) =>
        new() { JobName = jobName, LastRunAt = DateTime.UtcNow, Status = "NotRun" };

    public SimulatedJobStatus MarkFailed(string errorMessage) =>
        new() { Id = Id, JobName = JobName, LastRunAt = DateTime.UtcNow, Status = "Failed", ErrorMessage = errorMessage };

    public SimulatedJobStatus MarkCompleted() =>
        new() { Id = Id, JobName = JobName, LastRunAt = DateTime.UtcNow, Status = "Completed", ErrorMessage = null };
}
