using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// The bearer token every request needs (PLAN.md §6.1): 256 random bits, base64url, new on every engine start. It
/// is only ever written to the discovery file; the log redacts it.
/// </summary>
public sealed class ApiToken
{
    public const string Scheme = "Bearer";

    private readonly byte[] _bytes;

    public ApiToken()
        : this(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)))
    {
    }

    public ApiToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
        _bytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    /// <summary>Whether an <c>Authorization</c> header value is <c>Bearer &lt;token&gt;</c>, compared in constant time.</summary>
    public bool Matches(string? authorization)
    {
        if (authorization is null
            || authorization.Length <= Scheme.Length + 1
            || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || authorization[Scheme.Length] != ' ')
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorization.AsSpan(Scheme.Length + 1).Trim().ToString());
        return CryptographicOperations.FixedTimeEquals(presented, _bytes);
    }
}
