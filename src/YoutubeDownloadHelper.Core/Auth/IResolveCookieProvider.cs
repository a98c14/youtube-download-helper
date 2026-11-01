namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// The cookies file for a resolve or playlist listing that runs outside a job (<c>POST /resolve</c>, playlist
/// expansion in <c>POST /items</c>; PLAN.md §5.7), so members-only entries and private playlists resolve when the user
/// is logged in. <see cref="NoResolveCookieProvider"/> is the default (no cookies); the cookie writeback work (#48)
/// replaces it.
/// </summary>
public interface IResolveCookieProvider
{
    /// <summary>Writes a cookies file for one run and returns its path, or returns <see langword="null"/> to run without cookies.</summary>
    Task<string?> BeforeResolveAsync(CancellationToken cancellationToken);

    /// <summary>Reads the cookies yt-dlp wrote back, merges them, and deletes the file. Called with no cancellation.</summary>
    /// <param name="cookiesFile">What <see cref="BeforeResolveAsync"/> returned.</param>
    /// <param name="failedWithAuth">The run failed with an <c>Auth</c> error.</param>
    /// <param name="cancellationToken">Not cancelled by the caller.</param>
    Task AfterResolveAsync(string? cookiesFile, bool failedWithAuth, CancellationToken cancellationToken);
}

/// <summary>Resolves without cookies.</summary>
public sealed class NoResolveCookieProvider : IResolveCookieProvider
{
    public Task<string?> BeforeResolveAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task AfterResolveAsync(string? cookiesFile, bool failedWithAuth, CancellationToken cancellationToken) => Task.CompletedTask;
}
