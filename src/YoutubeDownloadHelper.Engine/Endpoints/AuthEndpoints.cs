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
                "Only youtube.com cookies are kept; they replace the stored ones as a new login. Items waiting on auth are " +
                "queued again. 422 auth.noYouTubeCookies when there is no youtube.com cookie.");
        endpoints.MapDelete(ApiRoutes.AuthCookies, LogOutAsync)
            .WithTags("Auth")
            .WithName("deleteCookies")
            .WithSummary("Logs out: deletes the stored cookies.");
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
