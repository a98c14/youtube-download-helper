namespace YoutubeDownloadHelper.Core.Scheduling;

/// <summary>
/// The connectivity check behind the <c>WaitingForNetwork</c> queue block (PLAN.md §4.3). <see cref="HttpNetworkProbe"/>
/// is the default; tests use a fake (no real network in automated tests).
/// </summary>
public interface INetworkProbe
{
    /// <summary>Whether YouTube is reachable. Never throws for network trouble; that is <see langword="false"/>.</summary>
    Task<bool> IsOnlineAsync(CancellationToken cancellationToken);
}

/// <summary>Online when <c>https://www.youtube.com/generate_204</c> answers with any HTTP status within 10 seconds.</summary>
public sealed class HttpNetworkProbe : INetworkProbe, IDisposable
{
    private static readonly Uri ProbeUri = new("https://www.youtube.com/generate_204");

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ProbeUri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout.
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
