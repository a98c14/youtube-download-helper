using Microsoft.Net.Http.Headers;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// The engine's request checks (PLAN.md §6.1), in order, for every request including the OpenAPI document:
/// <list type="number">
/// <item>Any <c>Origin</c> header → 403. Browsers send it on cross-origin requests; the engine has no CORS.</item>
/// <item>A <c>Host</c> other than <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c> → 403 (DNS rebinding).</item>
/// <item>No <c>Authorization: Bearer &lt;token&gt;</c> → 401.</item>
/// </list>
/// </summary>
public sealed class RequestGuardMiddleware(RequestDelegate next, EngineEndpoint endpoint, ApiToken token)
{
    private static readonly EngineMessage OriginNotAllowed =
        new(ApiErrorCodes.OriginNotAllowed, "Requests with an Origin header are not accepted.");

    private static readonly EngineMessage HostNotAllowed =
        new(ApiErrorCodes.HostNotAllowed, "The Host header must be 127.0.0.1:<port> or localhost:<port>.");

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;

        if (request.Headers.ContainsKey(HeaderNames.Origin))
        {
            return ApiErrors.WriteAsync(context, StatusCodes.Status403Forbidden, OriginNotAllowed);
        }

        if (!IsAllowedHost(request.Host))
        {
            return ApiErrors.WriteAsync(context, StatusCodes.Status403Forbidden, HostNotAllowed);
        }

        if (!token.Matches(request.Headers.Authorization.Count == 1 ? request.Headers.Authorization[0] : null))
        {
            context.Response.Headers.WWWAuthenticate = ApiToken.Scheme;
            return ApiErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, ApiErrors.ForStatus(StatusCodes.Status401Unauthorized));
        }

        return next(context);
    }

    private bool IsAllowedHost(HostString host) =>
        endpoint.Port is { } port
        && host.Port == port
        && (string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
