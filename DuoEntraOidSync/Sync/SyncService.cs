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

#if DEBUG
        var noUpn = new List<string>();
        var unmatched = new List<string>();
#endif

        foreach (var user in duoUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var upn = user.GetAlias(upnSlot)?.Trim();
            if (string.IsNullOrEmpty(upn))
            {
                result.SkippedNoUpn++;
#if DEBUG
                noUpn.Add($"{user.UserId} ({user.Username})");
#endif
                continue;
            }

            if (!entraIndex.TryGetValue(upn, out var oid))
            {
                result.Unmatched++;
#if DEBUG
                unmatched.Add($"{user.UserId} ({user.Username}) UPN '{upn}'");
#endif
                continue;
            }

            result.Matched++;

            var current = user.GetAlias(oidSlot);
            if (string.Equals(current, oid, StringComparison.OrdinalIgnoreCase))
            {
                result.AlreadyCurrent++;
                continue;
            }

            if (_sync.DryRun)
            {
                result.Updated++;
                _logger.LogInformation(
                    "[dry-run] Would set {Slot}={Oid} on Duo user {UserId} (UPN {Upn}, was '{Current}').",
                    oidSlot, oid, user.UserId, upn, current ?? "<unset>");
                continue;
            }

            try
            {
                await _duo.UpdateAliasAsync(user.UserId, oidSlot, oid, cancellationToken);
                result.Updated++;
                _logger.LogInformation(
                    "Set {Slot}={Oid} on Duo user {UserId} (UPN {Upn}, was '{Current}').",
                    oidSlot, oid, user.UserId, upn, current ?? "<unset>");
            }
            catch (DuoApiException ex)
            {
                result.Failed++;
                _logger.LogError(ex, "Failed to update {Slot} on Duo user {UserId} (UPN {Upn}).", oidSlot, user.UserId, upn);
            }
        }

        _logger.LogInformation(
            "Sync complete. Duo users: {DuoUsers}, matched: {Matched}, updated: {Updated}, already current: {AlreadyCurrent}, unmatched: {Unmatched}, no UPN: {SkippedNoUpn}, failed: {Failed}.",
            result.DuoUsers, result.Matched, result.Updated, result.AlreadyCurrent, result.Unmatched, result.SkippedNoUpn, result.Failed);

#if DEBUG
        if (noUpn.Count > 0)
        {
            _logger.LogInformation("No UPN ({Count}):\n  {List}", noUpn.Count, string.Join("\n  ", noUpn));
        }

        if (unmatched.Count > 0)
        {
            _logger.LogInformation("Unmatched ({Count}):\n  {List}", unmatched.Count, string.Join("\n  ", unmatched));
        }
#endif

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
}
