using System.Diagnostics;
using DuoEntraOidSync.Configuration;
using DuoEntraOidSync.Duo;
using DuoEntraOidSync.Graph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuoEntraOidSync.Sync;

/// <summary>
/// One sync pass: pull Entra + Duo, join on the UPN alias slot, and stamp the
/// Entra oid into the oid alias slot wherever it is missing or stale.
/// </summary>
public sealed class SyncService
{
    private readonly EntraUserService _entra;
    private readonly DuoAdminClient _duo;
    private readonly SyncOptions _sync;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        EntraUserService entra,
        DuoAdminClient duo,
        IOptions<SyncOptions> sync,
        ILogger<SyncService> logger)
    {
        _entra = entra;
        _duo = duo;
        _sync = sync.Value;
        _logger = logger;
    }

    public async Task<SyncResult> RunAsync(CancellationToken cancellationToken)
    {
        // From the top of the run, because functionTimeout is too: the pulls below spend
        // the same budget the writes do.
        var started = Stopwatch.GetTimestamp();
        var upnSlot = _sync.DuoUpnAliasSlot;
        var oidSlot = _sync.DuoOidAliasSlot;

        if (string.Equals(upnSlot, oidSlot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DuoOidAliasSlot ('{oidSlot}') must differ from DuoUpnAliasSlot ('{upnSlot}'); writing the oid there would clobber the join key.");
        }

        _logger.LogInformation("Pulling users from Entra via Microsoft Graph...");
        var entraIndex = await _entra.BuildOidIndexAsync(cancellationToken);

        _logger.LogInformation("Pulling users from Duo...");
        var duoUsers = await _duo.ListUsersAsync(cancellationToken);
        _logger.LogInformation("Pulled {DuoCount} Duo users.", duoUsers.Count);

        var result = new SyncResult { DryRun = _sync.DryRun, DuoUsers = duoUsers.Count };

        var budget = _sync.TimeBudget > TimeSpan.Zero ? _sync.TimeBudget : (TimeSpan?)null;

        foreach (var user in duoUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var upn = user.GetAlias(upnSlot)?.Trim();
            if (string.IsNullOrEmpty(upn))
            {
                result.SkippedNoUpn++;
                _logger.LogDebug("Duo user {UserId} ({Username}) has no UPN in {Slot}.", user.UserId, user.Username, upnSlot);
                continue;
            }

            if (!entraIndex.TryGetValue(upn, out var oid))
            {
                result.Unmatched++;
                _logger.LogDebug("Duo user {UserId} ({Username}) UPN '{Upn}' not found in the Entra index.", user.UserId, user.Username, upn);
                continue;
            }

            result.Matched++;

            var current = user.GetAlias(oidSlot);
            if (string.Equals(current, oid, StringComparison.OrdinalIgnoreCase))
            {
                result.AlreadyCurrent++;
                continue;
            }

            // Out of time. Keep walking rather than breaking, so the counts below still
            // describe the whole directory; the next run picks the deferred writes up.
            // Dry runs issue no writes, so they are never rate-limited and never defer.
            if (!_sync.DryRun && budget is { } limit && Stopwatch.GetElapsedTime(started) >= limit)
            {
                if (!result.BudgetExpired)
                {
                    result.BudgetExpired = true;
                    _logger.LogWarning(
                        "Time budget of {Budget} exhausted after {Updated} update(s); deferring the remaining writes to the next run.",
                        limit, result.Updated);
                }

                result.Deferred++;
                continue;
            }

            try
            {
                if (!_sync.DryRun)
                {
                    await _duo.UpdateAliasAsync(user.UserId, oidSlot, oid, cancellationToken);
                }

                result.Updated++;
                _logger.LogInformation(
                    "{Action}{Slot}={Oid} on Duo user {UserId} (UPN {Upn}, was '{Current}').",
                    _sync.DryRun ? "[dry-run] Would set " : "Set ",
                    oidSlot, oid, user.UserId, upn, current ?? "<unset>");
            }
            catch (DuoApiException ex)
            {
                result.Failed++;
                _logger.LogError(ex, "Failed to update {Slot} on Duo user {UserId} (UPN {Upn}).", oidSlot, user.UserId, upn);
            }
        }

        _logger.LogInformation(
            "Sync {Outcome}. Duo users: {DuoUsers}, matched: {Matched}, updated: {Updated}, already current: {AlreadyCurrent}, unmatched: {Unmatched}, no UPN: {SkippedNoUpn}, deferred: {Deferred}, failed: {Failed}.",
            result.Outcome,
            result.DuoUsers, result.Matched, result.Updated, result.AlreadyCurrent, result.Unmatched, result.SkippedNoUpn, result.Deferred, result.Failed);

        return result;
    }
}

public sealed class SyncResult
{
    public bool DryRun { get; set; }
    public int DuoUsers { get; set; }
    public int Matched { get; set; }
    public int Updated { get; set; }
    public int AlreadyCurrent { get; set; }
    public int Unmatched { get; set; }
    public int SkippedNoUpn { get; set; }
    public int Failed { get; set; }
    public int Deferred { get; set; }
    public bool BudgetExpired { get; set; }

    public string Outcome => BudgetExpired ? "partial" : "complete";
}
