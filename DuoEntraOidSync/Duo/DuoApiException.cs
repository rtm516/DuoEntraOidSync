namespace DuoEntraOidSync.Duo;

/// <summary>
/// Raised when the Duo Admin API returns a non-OK stat or a transport error.
/// </summary>
public sealed class DuoApiException : Exception
{
    public DuoApiException(int httpStatus, int? code, string? message, string? messageDetail)
        : base($"Duo API error (HTTP {httpStatus}, code {code?.ToString() ?? "n/a"}): {message} {messageDetail}".Trim())
    {
        HttpStatus = httpStatus;
        Code = code;
        DuoMessage = message;
        MessageDetail = messageDetail;
    }

    public int HttpStatus { get; }

    public int? Code { get; }

    public string? DuoMessage { get; }

    public string? MessageDetail { get; }
}
