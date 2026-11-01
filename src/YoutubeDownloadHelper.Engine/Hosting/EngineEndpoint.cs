using System.Globalization;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Where the engine listens: always <c>127.0.0.1</c>. <see cref="RequestedPort"/> is chosen before the server starts;
/// <see cref="Port"/> is the port it actually got, known once it listens.
/// </summary>
public sealed class EngineEndpoint
{
    private volatile string? _baseUrl;
    private int _port;

    /// <summary>The configured port; <c>0</c> for a dynamic one.</summary>
    public int RequestedPort { get; set; }

    /// <summary>The port the server listens on, or <see langword="null"/> before it listens.</summary>
    public int? Port => _baseUrl is null ? null : _port;

    /// <summary><c>http://127.0.0.1:&lt;port&gt;</c>, or <see langword="null"/> before the server listens.</summary>
    public string? BaseUrl => _baseUrl;

    public void SetListening(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _port = port;
        _baseUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");
    }
}
