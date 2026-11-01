using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>Every API error response is an <see cref="EngineMessage"/>: <c>{ code, args, message }</c>.</summary>
public static class ApiErrors
{
    public static async Task WriteAsync(HttpContext context, int status, EngineMessage message)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(message, options, context.RequestAborted);
    }

    /// <summary>For a status code the endpoints set without a body (no route, wrong method, …).</summary>
    public static Task WriteForStatusAsync(StatusCodeContext statusContext)
    {
        ArgumentNullException.ThrowIfNull(statusContext);
        var context = statusContext.HttpContext;
        var status = context.Response.StatusCode;
        return WriteAsync(context, status, ForStatus(status));
    }

    /// <summary>For an unhandled exception. The exception itself is logged by the exception handler, never returned.</summary>
    public static Task WriteInternalAsync(HttpContext context) =>
        WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            new EngineMessage(ApiErrorCodes.Internal, "An unexpected engine error occurred. See the engine log."));

    /// <summary>A 400 body for a parameter or field with a value the engine does not accept.</summary>
    public static EngineMessage InvalidParameter(string parameter, string message) =>
        new(ApiErrorCodes.InvalidParameter, new Dictionary<string, string>(StringComparer.Ordinal) { ["parameter"] = parameter }, message);

    public static EngineMessage ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => new(ApiErrorCodes.BadRequest, "The request is malformed."),
        StatusCodes.Status401Unauthorized => new(ApiErrorCodes.Unauthorized, "A valid bearer token is required."),
        StatusCodes.Status404NotFound => new(ApiErrorCodes.NotFound, "No such route."),
        StatusCodes.Status405MethodNotAllowed => new(ApiErrorCodes.MethodNotAllowed, "The route does not take this method."),
        StatusCodes.Status500InternalServerError => new(ApiErrorCodes.Internal, "An unexpected engine error occurred. See the engine log."),
        _ => new(
            ApiErrorCodes.HttpError,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = status.ToString(CultureInfo.InvariantCulture) },
            string.Create(CultureInfo.InvariantCulture, $"The request failed with HTTP status {status}.")),
    };
}
