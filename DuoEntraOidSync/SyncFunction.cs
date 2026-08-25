using DuoEntraOidSync.Sync;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DuoEntraOidSync;

public sealed class SyncFunction
{
    private readonly SyncService _service;
    private readonly ILogger<SyncFunction> _logger;

    public SyncFunction(SyncService service, ILogger<SyncFunction> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Syncs Entra oids into the Duo oid alias slot on the schedule in the
    /// "Sync__Schedule" app setting (default "0 30 * * * *" — half past every hour).
    /// </summary>
    [Function("Sync")]
    public async Task Run(
        [TimerTrigger("%Sync__Schedule%"
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

        await _service.RunAsync(cancellationToken);
    }
}
