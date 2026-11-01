namespace YoutubeDownloadHelper.Contracts.Api;

/// <summary>
/// Codes of the <see cref="Messages.EngineMessage"/> body that every API error response carries
/// (<c>{ code, args, message }</c>). No args unless listed.
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>401: the <c>Authorization: Bearer</c> token is missing or wrong.</summary>
    public const string Unauthorized = "api.unauthorized";

    /// <summary>
    /// 403: the <c>Host</c> header is not <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c> (DNS-rebinding
    /// defense).
    /// </summary>
    public const string HostNotAllowed = "api.hostNotAllowed";

    /// <summary>403: the request has an <c>Origin</c> header, so it comes from a web page. There is no CORS.</summary>
    public const string OriginNotAllowed = "api.originNotAllowed";

    /// <summary>404: no such route.</summary>
    public const string NotFound = "api.notFound";

    /// <summary>405: the route does not take this method.</summary>
    public const string MethodNotAllowed = "api.methodNotAllowed";

    /// <summary>400: the request is malformed.</summary>
    public const string BadRequest = "api.badRequest";

    /// <summary>
    /// 400: a query parameter or body field has a value the engine does not accept (e.g. an unknown preset, a bad
    /// cursor). Args: <c>parameter</c> (its camelCase name).
    /// </summary>
    public const string InvalidParameter = "api.invalidParameter";

    /// <summary>Any other 4xx/5xx status without a more specific code. Args: <c>status</c>.</summary>
    public const string HttpError = "api.httpError";

    /// <summary>500: an unexpected engine error. Details are in the engine log.</summary>
    public const string Internal = "api.internal";
}
