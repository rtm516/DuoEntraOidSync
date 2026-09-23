using System.Diagnostics;
using DuoEntraOidSync.Sync;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DuoEntraOidSync;

public sealed class SyncFunction
{
    private readonly SyncService _service;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<SyncFunction> _logger;

    public SyncFunction(SyncService service, TelemetryClient telemetry, ILogger<SyncFunction> logger)
    {
        _service = service;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Syncs Entra oids into the Duo oid alias slot on the schedule in the
    /// "Sync__Schedule" app setting (default "0 30 * * * *" — half past every hour).
    /// The %...% expression uses the config-key form "Sync:Schedule": configuration
    /// binding remaps the "Sync__Schedule" app setting / env var onto that key.
    /// </summary>
    [Function("Sync")]
    public async Task Run(
        [TimerTrigger("%Sync:Schedule%"
#if DEBUG
            , RunOnStartup = true // Debug/F5 only — fires on launch so you don't wait for the schedule.
#endif
        )] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sync triggered at {Now:o}.", DateTimeOffset.UtcNow);

        if (timer.IsPastDue)
        {
            _logger.LogWarning("Sync timer is running past due.");
        }

        var started = Stopwatch.GetTimestamp();
        var result = await _service.RunAsync(cancellationToken);

        // One custom event per run, so the outcome is queryable and chartable without
        // parsing the summary trace. Counts go in metrics (numeric, aggregatable in KQL
        // and usable as a metric alert); mode goes in properties for filtering.
        _telemetry.TrackEvent(
            "SyncCompleted",
            properties: new Dictionary<string, string>
            {
                ["Mode"] = result.DryRun ? "dry-run" : "live",
                ["Outcome"] = result.BudgetExpired ? "partial" : "complete",
            },
            metrics: new Dictionary<string, double>
            {
                ["DuoUsers"] = result.DuoUsers,
                ["Matched"] = result.Matched,
                ["Updated"] = result.Updated,
                ["AlreadyCurrent"] = result.AlreadyCurrent,
                ["Unmatched"] = result.Unmatched,
                ["SkippedNoUpn"] = result.SkippedNoUpn,
                ["Deferred"] = result.Deferred,
                ["Failed"] = result.Failed,
                ["DurationSeconds"] = Stopwatch.GetElapsedTime(started).TotalSeconds,
            });
    }
}
