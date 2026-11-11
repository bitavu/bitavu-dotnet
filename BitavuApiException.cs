using System.Net;

namespace Bitavu;

/// <summary>A failed response or an unexpected response format from the Bitavu API.</summary>
public sealed class BitavuApiException : HttpRequestException
{
    internal BitavuApiException(
        HttpStatusCode statusCode, string? code, Guid? requestId, TimeSpan? retryAfter, bool invalidResponse = false)
        : base(invalidResponse ? "Bitavu returned an unexpected response." : $"Bitavu API returned HTTP {(int)statusCode}.",
            inner: null, statusCode)
    {
        Code = code;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }

    /// <summary>The API's error code, when available.</summary>
    public string? Code { get; }

    /// <summary>The server request identifier, when supplied by the API.</summary>
    public Guid? RequestId { get; }

    /// <summary>The requested delay before retrying, when supplied by the API.</summary>
    public TimeSpan? RetryAfter { get; }
}
