using System.Security.Cryptography;
using System.Text;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>Encrypts data at rest for the current Windows user.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <exception cref="CryptographicException">The data is corrupt or was protected for another user.</exception>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// DPAPI with <see cref="DataProtectionScope.CurrentUser"/>: only the same Windows user (on the same machine, or
/// with a roaming profile) can decrypt. A fixed per-purpose entropy keeps other DPAPI blobs of this user from being
/// passed off as the cookie jar.
/// </summary>
public sealed class DpapiSecretProtector(string purpose) : ISecretProtector
{
    public const string CookieJarPurpose = "YoutubeDownloadHelper.Auth.CookieJar.v1";

    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(purpose);

    public DpapiSecretProtector()
        : this(CookieJarPurpose)
    {
    }

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, _entropy, DataProtectionScope.CurrentUser);
}
