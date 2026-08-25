using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuoEntraOidSync.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuoEntraOidSync.Duo;

/// <summary>
/// Minimal Duo Admin API client with hand-rolled HMAC-SHA1 request signing
/// (Duo has no first-party .NET SDK). Implements the canonical-request scheme:
/// five newline-joined lines (RFC 2822 date, upper-case method, lower-case host,
/// path, sorted URL-encoded params), signed with the skey and sent as HTTP Basic
/// auth (ikey:hex-signature).
/// </summary>
public sealed class DuoAdminClient
{
    private const int PageLimit = 300; // Duo max page size.
    private const int MaxRateLimitRetries = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly DuoOptions _options;
    private readonly ILogger<DuoAdminClient> _logger;

    public DuoAdminClient(HttpClient http, IOptions<DuoOptions> options, ILogger<DuoAdminClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Pulls every Duo user, following pagination.</summary>
    public async Task<IReadOnlyList<DuoUser>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = new List<DuoUser>();
        var offset = 0;

        while (true)
        {
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["limit"] = PageLimit.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
            };

            var envelope = await SendAsync<List<DuoUser>>(HttpMethod.Get, "/admin/v1/users", parameters, cancellationToken);
            if (envelope.Response is { Count: > 0 })
            {
                users.AddRange(envelope.Response);
            }

            var next = envelope.Metadata?.NextOffset;
            if (next is null)
            {
                break;
            }

            offset = next.Value;
        }

        return users;
    }

    /// <summary>
    /// Sets a single alias slot on a Duo user. Only the named slot is sent, so the
    /// other alias slots (including the UPN join key) are left untouched.
    /// </summary>
    public async Task UpdateAliasAsync(string userId, string aliasSlot, string value, CancellationToken cancellationToken)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [aliasSlot] = value,
        };

        await SendAsync<DuoUser>(HttpMethod.Post, $"/admin/v1/users/{Uri.EscapeDataString(userId)}", parameters, cancellationToken);
    }

    private async Task<DuoEnvelope<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        SortedDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var canonicalParams = Canonicalize(parameters);

        for (var attempt = 0; ; attempt++)
        {
            // The Date header and the date line in the signature must be byte-identical.
            var date = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " -0000";
            using var request = BuildRequest(method, path, canonicalParams, date);

            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var delay = GetRetryDelay(response, attempt);
                _logger.LogWarning("Duo rate limited (429) on {Method} {Path}; retrying in {Delay}s (attempt {Attempt}).",
                    method.Method, path, delay.TotalSeconds, attempt + 1);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var envelope = Deserialize<T>(body, (int)response.StatusCode);

            if (!string.Equals(envelope.Stat, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new DuoApiException((int)response.StatusCode, envelope.Code, envelope.Message, envelope.MessageDetail);
            }

            return envelope;
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string canonicalParams, string date)
    {
        var host = _options.ApiHost.ToLowerInvariant();

        var canonical = string.Join('\n',
            date,
            method.Method.ToUpperInvariant(),
            host,
            path,
            canonicalParams);

        var signature = Sign(canonical);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.IntegrationKey}:{signature}"));

        var isGet = method == HttpMethod.Get || method == HttpMethod.Delete;
        var uri = isGet && canonicalParams.Length > 0
            ? $"https://{host}{path}?{canonicalParams}"
            : $"https://{host}{path}";

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.TryAddWithoutValidation("Date", date);
        request.Headers.UserAgent.ParseAdd("DuoEntraOidSync/1.0");

        if (!isGet)
        {
            request.Content = new StringContent(canonicalParams);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }

        return request;
    }

    private string Sign(string canonical)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_options.SecretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Sorted, percent-encoded "key=value&amp;..." string. Uri.EscapeDataString gives the
    /// RFC 3986 encoding Duo expects (uppercase hex; unreserved set A-Za-z0-9-._~).
    /// </summary>
    private static string Canonicalize(SortedDictionary<string, string> parameters)
        => string.Join('&', parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        // Duo recommends exponential backoff when Retry-After is absent.
        return TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
    }

    private static DuoEnvelope<T> Deserialize<T>(string body, int httpStatus)
    {
        try
        {
            return JsonSerializer.Deserialize<DuoEnvelope<T>>(body, JsonOptions)
                   ?? throw new DuoApiException(httpStatus, null, "Empty response body", null);
        }
        catch (JsonException ex)
        {
            throw new DuoApiException(httpStatus, null, "Unparseable response body", ex.Message);
        }
    }

    private sealed class DuoEnvelope<T>
    {
        [JsonPropertyName("stat")]
        public string Stat { get; set; } = string.Empty;

        [JsonPropertyName("response")]
        public T? Response { get; set; }

        [JsonPropertyName("metadata")]
        public DuoMetadata? Metadata { get; set; }

        [JsonPropertyName("code")]
        public int? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("message_detail")]
        public string? MessageDetail { get; set; }
    }

    private sealed class DuoMetadata
    {
        [JsonPropertyName("next_offset")]
        public int? NextOffset { get; set; }

        [JsonPropertyName("total_objects")]
        public int? TotalObjects { get; set; }
    }
}
