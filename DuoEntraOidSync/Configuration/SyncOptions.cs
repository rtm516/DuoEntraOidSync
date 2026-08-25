namespace DuoEntraOidSync.Configuration;

/// <summary>
/// Behavioural options for the reconcile run. Bound from the "Sync" section.
/// </summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// Duo alias slot holding the UPN (the join key looked up in Entra).
    /// Defaults to alias1.
    /// </summary>
    public string DuoUpnAliasSlot { get; set; } = "alias1";

    /// <summary>
    /// Duo alias slot the Entra object id (oid) is written into.
    /// Defaults to alias4. Must differ from <see cref="DuoUpnAliasSlot"/>.
    /// </summary>
    public string DuoOidAliasSlot { get; set; } = "alias2";

    /// <summary>
    /// When true, log the writes that would happen but do not call Duo.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Client id of the user-assigned managed identity to authenticate to Graph.
    /// Leave null to use the system-assigned identity (or local dev credentials).
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }
}
