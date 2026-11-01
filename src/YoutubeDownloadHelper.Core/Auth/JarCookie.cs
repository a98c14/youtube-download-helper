namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// One cookie, as a Netscape <c>cookies.txt</c> line holds it. <see cref="Domain"/> is stored in lower case, so
/// keys compare ordinally. Fields cannot contain tabs or line breaks, so every cookie can be written back out.
/// </summary>
public sealed record JarCookie
{
    private readonly string _domain = "";
    private readonly string _path = "/";
    private readonly string _name = "";
    private readonly string _value = "";
    private readonly DateTimeOffset? _expires;

    public JarCookie(string domain, string path, string name, string value)
    {
        Domain = domain;
        Path = path;
        Name = name;
        Value = value;
    }

    /// <summary>The domain in lower case. A leading dot makes it a domain cookie; without one it is host-only.</summary>
    public string Domain
    {
        get => _domain;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Domain));
            _domain = Checked(value, nameof(Domain)).ToLowerInvariant();
        }
    }

    public string Path
    {
        get => _path;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(Path));
            _path = Checked(value, nameof(Path));
        }
    }

    public string Name
    {
        get => _name;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(Name));
            _name = Checked(value, nameof(Name));
        }
    }

    public string Value
    {
        get => _value;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Value));
            _value = Checked(value, nameof(Value));
        }
    }

    /// <summary>The Netscape "include subdomains" flag (<c>TRUE</c> for domain cookies in browser exports).</summary>
    public bool IncludeSubdomains { get; init; }

    public bool Secure { get; init; }

    /// <summary>Written as the <c>#HttpOnly_</c> domain prefix.</summary>
    public bool HttpOnly { get; init; }

    /// <summary>
    /// When the cookie expires, or <see langword="null"/> for a session cookie. Truncated to whole seconds (UTC), as
    /// a cookies file holds it, so a jar compares equal after a round trip through one.
    /// </summary>
    public DateTimeOffset? Expires
    {
        get => _expires;
        init => _expires = value is { } v ? DateTimeOffset.FromUnixTimeSeconds(v.ToUnixTimeSeconds()) : null;
    }

    public CookieKey Key => new(Domain, Path, Name);

    public bool IsExpired(DateTimeOffset now) => Expires is { } expires && expires <= now;

    // The value is never part of the message: it is a secret.
    private static string Checked(string value, string field) =>
        value.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0
            ? throw new ArgumentException($"A cookie's {field} cannot contain tabs or line breaks.", field)
            : value;
}

/// <summary>What identifies a cookie in a jar: domain (lower case), path and name, compared ordinally.</summary>
public readonly record struct CookieKey(string Domain, string Path, string Name)
{
    public override string ToString() => $"{Domain}{Path} {Name}";
}
