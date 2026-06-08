using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MonitorPedidos.Web.Telemetry;

public sealed class MonitorMetrics
{
    private readonly Counter<int>      _checkRuns;
    private readonly Histogram<double> _checkDuration;

    public MonitorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("MonitorPedidos.Checkers");

        _checkRuns = meter.CreateCounter<int>(
            "monitor.checker.runs",
            description: "Ejecuciones de cada checker por módulo y estado");

        _checkDuration = meter.CreateHistogram<double>(
            "monitor.checker.duration_ms",
            unit: "ms",
            description: "Duración de cada ejecución de checker en milisegundos");
    }

    public void RecordCheck(string module, string status, double durationMs)
    {
        var tags = new TagList
        {
            { "module", module },
            { "status", status }   // OK | Warning | Critical
        };
        _checkRuns.Add(1, tags);
        _checkDuration.Record(durationMs, new TagList { { "module", module } });
    }
}
