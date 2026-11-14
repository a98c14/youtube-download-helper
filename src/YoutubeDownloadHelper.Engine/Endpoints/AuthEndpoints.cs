using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.Auth, GetStatus)
            .WithTags("Auth")
            .WithName("getAuth")
            .WithSummary("The YouTube login status.");
        endpoints.MapPut(ApiRoutes.AuthCookies, SetCookiesAsync)
            .WithTags("Auth")
            .WithName("setCookies")
            .WithSummary("Stores new cookies: the login window's (cookies) or an imported cookies.txt (netscape), exactly one of them.")
            .WithDescription(
                "Only youtube.com cookies are kept; they replace the stored ones as a new login, and a verification starts " +
                "in the background. Items waiting on auth are queued again once it passes. 422 auth.noYouTubeCookies when " +
                "there is no youtube.com cookie.");
        endpoints.MapDelete(ApiRoutes.AuthCookies, LogOutAsync)
            .WithTags("Auth")
            .WithName("deleteCookies")
            .WithSummary("Logs out: deletes the stored cookies.");
        endpoints.MapPost(ApiRoutes.AuthVerify, VerifyAsync)
            .WithTags("Auth")
            .WithName("verifyAuth")
            .WithSummary("Verifies the stored cookies now, with the recovery ladder.")
            .WithDescription(
                "Answers when the verification is done, or as soon as the engine waits for a browser refresh it asked for " +
                "(recovering: true; auth.changed follows with the outcome).");
        endpoints.MapPost(ApiRoutes.AuthRefreshBegin, BeginRefreshAsync)
            .WithTags("Auth")
            .WithName("beginAuthRefresh")
            .WithSummary("Starts a browser refresh: answers the current cookies and a lease.")
            .WithDescription(
                "New cookie-using job starts wait until the lease is completed or expires (at most 2 minutes); running jobs " +
                "continue. 409 auth.notLoggedIn without cookies, auth.refresh.inProgress while another lease is held.");
        endpoints.MapPut(ApiRoutes.AuthRefreshComplete, CompleteRefreshAsync)
            .WithTags("Auth")
            .WithName("completeAuthRefresh")
            .WithSummary("Hands back the refreshed cookies; they are kept only if they pass verification. Always ends the lease.")
            .WithDescription(
                "409 auth.refresh.noLease (unknown, completed or expired lease) or auth.refresh.superseded (logged in, " +
                "imported or logged out since); 422 auth.refresh.rejected (not logged in), auth.refresh.unverified (could " +
                "not be verified) or auth.noYouTubeCookies. On any error the stored cookies stay.");
        return endpoints;
    }

    private static Ok<AuthInfo> GetStatus(AuthManager auth) => TypedResults.Ok(auth.Status);

    private static async Task<Results<Ok<SetCookiesResponse>, BadRequest<EngineMessage>, UnprocessableEntity<EngineMessage>>> SetCookiesAsync(
        SetCookiesRequest request,
        AuthManager auth,
        CancellationToken cancellationToken)
    {
        if ((request.Cookies is null) == (request.Netscape is null))
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("cookies", "Send either cookies or netscape."));
        }

        CookieJar jar;
        IReadOnlyList<int> skipped = [];
        CookieSource source;
        if (request.Netscape is { } text)
        {
            var parsed = NetscapeCookieFile.Parse(text);
            (jar, skipped, source) = (parsed.Jar, parsed.SkippedLines, CookieSource.Import);
        }
        else if (ToJar(request.Cookies!) is { } fromLogin)
        {
            (jar, source) = (fromLogin, CookieSource.Login);
        }
        else
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter(
                "cookies", "Every cookie needs a domain, a path and a name, and no field may contain a tab or a line break."));
        }

        jar = jar.YouTubeOnly();
        if (jar.Count == 0)
        {
            return TypedResults.UnprocessableEntity(new EngineMessage(AuthCodes.NoYouTubeCookies, "There are no youtube.com cookies to store."));
        }

        var generation = await auth.SetCookiesAsync(jar, source, cancellationToken);
        return TypedResults.Ok(new SetCookiesResponse(auth.Status, generation.Jar.Count, skipped));
    }

    private static async Task<NoContent> LogOutAsync(AuthManager auth, CancellationToken cancellationToken)
    {
        await auth.LogOutAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<AuthVerifyResponse>> VerifyAsync(AuthVerifier verifier, CancellationToken cancellationToken)
    {
        var result = await verifier.VerifyAsync(cancellationToken);
        return TypedResults.Ok(new AuthVerifyResponse(result.Auth, result.Recovering));
    }

    private static async Task<Results<Ok<AuthRefreshBeginResponse>, Conflict<EngineMessage>>> BeginRefreshAsync(
        BrowserRefreshLease lease,
        CancellationToken cancellationToken)
    {
        var result = await lease.BeginAsync(cancellationToken);
        return result.Status switch
        {
            RefreshBeginStatus.Begun => TypedResults.Ok(
                new AuthRefreshBeginResponse(result.LeaseId!, result.ExpiresAt!.Value, [.. result.Jar!.Cookies.Select(ToInfo)])),
            RefreshBeginStatus.NotLoggedIn => TypedResults.Conflict(
                new EngineMessage(AuthCodes.NotLoggedIn, "No cookies are stored, so there is nothing to refresh.")),
            _ => TypedResults.Conflict(new EngineMessage(AuthCodes.RefreshInProgress, "Another browser refresh is in progress.")),
        };
    }

    private static async Task<Results<Ok<AuthRefreshCompleteResponse>, BadRequest<EngineMessage>, Conflict<EngineMessage>, UnprocessableEntity<EngineMessage>>> CompleteRefreshAsync(
        AuthRefreshCompleteRequest request,
        BrowserRefreshLease lease,
        AuthManager auth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseId))
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("leaseId", "The lease id from refresh/begin is required."));
        }

        if (request.Cookies is null || ToJar(request.Cookies) is not { } jar)
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter(
                "cookies", "Every cookie needs a domain, a path and a name, and no field may contain a tab or a line break."));
        }

        jar = jar.YouTubeOnly();
        if (jar.Count == 0)
        {
            return TypedResults.UnprocessableEntity(new EngineMessage(AuthCodes.NoYouTubeCookies, "There are no youtube.com cookies to store."));
        }

        var result = await lease.CompleteAsync(request.LeaseId, jar, cancellationToken);
        return result.Status switch
        {
            RefreshCompleteStatus.Accepted => TypedResults.Ok(new AuthRefreshCompleteResponse(auth.Status, result.Stored!.Jar.Count)),
            RefreshCompleteStatus.NoLease => TypedResults.Conflict(
                new EngineMessage(AuthCodes.RefreshNoLease, "The refresh lease is unknown, already completed or expired.")),
            RefreshCompleteStatus.Superseded => TypedResults.Conflict(
                new EngineMessage(AuthCodes.RefreshSuperseded, "The login changed since the refresh began; its cookies were not kept.")),
            RefreshCompleteStatus.Rejected => TypedResults.UnprocessableEntity(
                new EngineMessage(AuthCodes.RefreshRejected, "The refreshed cookies are not logged in; the stored ones stay.")),
            _ => TypedResults.UnprocessableEntity(
                new EngineMessage(AuthCodes.RefreshUnverified, "The refreshed cookies could not be verified; the stored ones stay.")),
        };
    }

    /// <summary>A stored cookie as the API shows it: a domain cookie gets a leading dot.</summary>
    private static CookieInfo ToInfo(JarCookie c) => new(
        c.IncludeSubdomains && !c.Domain.StartsWith('.') ? "." + c.Domain : c.Domain,
        c.Path,
        c.Name,
        c.Value,
        c.Secure,
        c.HttpOnly,
        c.Expires);

    /// <summary>The login window's cookies, or null when one is not a valid cookie. A leading dot makes a domain cookie.</summary>
    private static CookieJar? ToJar(IReadOnlyList<CookieInfo> cookies)
    {
        var jar = new List<JarCookie>(cookies.Count);
        foreach (var c in cookies)
        {
            if (c is null)
            {
                return null;
            }

            try
            {
                jar.Add(new JarCookie(c.Domain, c.Path, c.Name, c.Value)
                {
                    IncludeSubdomains = c.Domain.StartsWith('.'),
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                    Expires = c.Expires,
                });
            }
            catch (ArgumentException)
            {
                // Null or blank fields (JSON can send null for any of them), tabs or line breaks.
                return null;
            }
        }

        return new CookieJar(jar);
    }
}
