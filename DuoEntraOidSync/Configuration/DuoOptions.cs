using System.ComponentModel.DataAnnotations;

namespace DuoEntraOidSync.Configuration;

/// <summary>
/// Duo Admin API credentials. Bound from the "Duo" configuration section.
/// In Azure these values are Key Vault references resolved by the platform into
/// app settings (Duo__IntegrationKey, Duo__SecretKey, Duo__ApiHost); the code
/// only ever sees the resolved values, never the vault.
/// </summary>
public sealed class DuoOptions
{
    public const string SectionName = "Duo";

    /// <summary>Duo Admin API integration key (ikey).</summary>
    [Required]
    public string IntegrationKey { get; set; } = string.Empty;

    /// <summary>Duo Admin API secret key (skey) — used as the HMAC key. Sensitive.</summary>
    [Required]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Duo API hostname, e.g. api-XXXXXXXX.duosecurity.com (no scheme).</summary>
    [Required]
    public string ApiHost { get; set; } = string.Empty;
}
