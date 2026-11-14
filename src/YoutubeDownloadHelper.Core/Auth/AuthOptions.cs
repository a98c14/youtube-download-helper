using YoutubeDownloadHelper.Core.Runner;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// Timings of the auth verification, the keep-alive ping, the recovery ladder and the browser-refresh lease (PLAN.md
/// §5.7). The defaults are the product's starting values, to be tuned from the telemetry logs; tests shorten them.
/// </summary>
public sealed record AuthOptions
{
    /// <summary>How often the keep-alive ping verifies the cookies, even when idle.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>When a verification could not decide (offline, tools missing, another error), how soon it runs again.</summary>
    public TimeSpan InconclusiveRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The least time between a verification and the next one that an Auth-category job failure asks for. A burst of
    /// members-only failures causes one verification, not one each. A login, an import and <c>POST /auth/verify</c> are
    /// not throttled.
    /// </summary>
    public TimeSpan ErrorVerificationInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Step (c) of the recovery ladder: how long the engine waits for a client to begin the browser refresh it asked for.</summary>
    public TimeSpan RefreshStartTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a browser-refresh lease holds new cookie-using job starts at most.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The page the verification fetches flat; it lists entries only when logged in. Tests point it at the fake.</summary>
    public string VerificationUrl { get; init; } = YtDlpTemplates.SubscriptionsFeedUrl;
}
