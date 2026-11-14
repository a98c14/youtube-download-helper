using System.Globalization;
using System.Net;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Client;

/// <summary>
/// The engine answered with an error status. <see cref="Error"/> is its <c>{ code, args, message }</c> body
/// (<see cref="ApiErrorCodes"/> and the feature codes); clients localize from its code. Connection failures are
/// <see cref="HttpRequestException"/>s instead.
/// </summary>
public sealed class EngineApiException : Exception
{
    public EngineApiException(HttpStatusCode statusCode, EngineMessage error)
        : base(error?.Message)
    {
        ArgumentNullException.ThrowIfNull(error);
        StatusCode = statusCode;
        Error = error;
    }

    public EngineApiException()
        : this(HttpStatusCode.InternalServerError, Fallback(HttpStatusCode.InternalServerError))
    {
    }

    public EngineApiException(string message)
        : base(message)
    {
        StatusCode = HttpStatusCode.InternalServerError;
        Error = new EngineMessage(ApiErrorCodes.Internal, message);
    }

    public EngineApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = HttpStatusCode.InternalServerError;
        Error = new EngineMessage(ApiErrorCodes.Internal, message);
    }

    public HttpStatusCode StatusCode { get; }

    public EngineMessage Error { get; }

    /// <summary>The error's code, e.g. <c>queue.itemNotFound</c>.</summary>
    public string Code => Error.Code;

    /// <summary>The body for a response that had none the client could read.</summary>
    internal static EngineMessage Fallback(HttpStatusCode statusCode)
    {
        var status = ((int)statusCode).ToString(CultureInfo.InvariantCulture);
        return new EngineMessage(
            ApiErrorCodes.HttpError,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = status },
            $"The engine answered with HTTP status {status}.");
    }
}
