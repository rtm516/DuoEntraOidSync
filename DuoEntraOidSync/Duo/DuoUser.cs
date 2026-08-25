using System.Text.Json.Serialization;

namespace DuoEntraOidSync.Duo;

/// <summary>
/// Subset of a Duo user object returned by GET /admin/v1/users that we care about.
/// </summary>
public sealed class DuoUser
{
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>
    /// Alias slots as a dictionary, e.g. {"alias1": "joe@x.com", "alias4": "&lt;oid&gt;"}.
    /// Slots that are unset are omitted by the API.
    /// </summary>
    [JsonPropertyName("aliases")]
    public Dictionary<string, string?>? Aliases { get; set; }

    /// <summary>
    /// Returns the value of the named alias slot, or null if unset/blank.
    /// </summary>
    public string? GetAlias(string slot)
    {
        if (Aliases is not null && Aliases.TryGetValue(slot, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }
}
