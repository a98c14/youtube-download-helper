using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// The engine's cookie lineage (PLAN.md §5.7 "Session longevity" point 1): numbered generations, encrypted with
/// DPAPI (<see cref="ISecretProtector"/>) in <c>auth\cookies.bin</c>. The newest generation is the current jar.
/// <para>
/// Retention: the current generation, the <see cref="KnownGoodKept"/> newest known-good ones before it, and the
/// current generation's <see cref="CookieGeneration.RollbackTo"/> target. Everything else is dropped on the next
/// change.
/// </para>
/// <para>
/// Only <c>youtube.com</c> cookies are stored. Every method is thread-safe and writes the file before it returns
/// (write to a temporary file, then replace). The queue actor (#48) serializes the callers anyway, so that a writeback
/// merge reads and replaces the current jar in one step.
/// </para>
/// </summary>
public sealed partial class CookieJarStore(
    DataPaths paths,
    ISecretProtector protector,
    TimeProvider time,
    ILogger<CookieJarStore> logger)
{
    /// <summary>How many known-good generations are kept besides the current one.</summary>
    public const int KnownGoodKept = 3;

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly Lock _gate = new();

    // Newest first. Null until loaded.
    private List<CookieGeneration>? _generations;
    private long _nextNumber;

    /// <summary>The current jar, or <see langword="null"/> when nothing is stored (not logged in).</summary>
    public CookieGeneration? Current
    {
        get
        {
            lock (_gate)
            {
                return Loaded().FirstOrDefault();
            }
        }
    }

    /// <summary>Every kept generation, newest (current) first.</summary>
    public IReadOnlyList<CookieGeneration> Generations
    {
        get
        {
            lock (_gate)
            {
                return [.. Loaded()];
            }
        }
    }

    /// <summary>
    /// What <see cref="RollBack"/> would restore: the generation the latest browser refresh in the current lineage
    /// started from, or <see langword="null"/> when there is none. Generations older than that refresh hold a dead
    /// <c>*PSIDTS</c> and are never rollback targets.
    /// </summary>
    public CookieGeneration? RollbackTarget
    {
        get
        {
            lock (_gate)
            {
                return TargetOf(Loaded());
            }
        }
    }

    public CookieGeneration? Find(long number)
    {
        lock (_gate)
        {
            return Loaded().Find(g => g.Number == number);
        }
    }

    /// <summary>
    /// Stores <paramref name="jar"/> (its <c>youtube.com</c> cookies) as the new current generation.
    /// </summary>
    /// <param name="jar">The cookies.</param>
    /// <param name="source">Anything but <see cref="CookieSource.Rollback"/>, which <see cref="RollBack"/> creates.</param>
    /// <param name="basedOn">
    /// For a writeback, ping or browser refresh: the generation it was derived from (for a refresh, the one
    /// <c>/auth/refresh/begin</c> handed out). Defaults to the current generation. Ignored for a login or import,
    /// which start a new lineage.
    /// </param>
    /// <param name="knownGood">Whether the jar has just been verified.</param>
    /// <exception cref="ArgumentException">The jar has no <c>youtube.com</c> cookies, or the source is a rollback.</exception>
    /// <exception cref="InvalidOperationException">A derived jar, but nothing is stored to derive it from.</exception>
    public CookieGeneration Add(CookieJar jar, CookieSource source, long? basedOn = null, bool knownGood = false)
    {
        ArgumentNullException.ThrowIfNull(jar);
        if (source == CookieSource.Rollback)
        {
            throw new ArgumentException("Use RollBack() to restore a generation.", nameof(source));
        }

        jar = jar.YouTubeOnly();
        if (jar.Count == 0)
        {
            throw new ArgumentException("The jar has no youtube.com cookies.", nameof(jar));
        }

        lock (_gate)
        {
            var generations = Loaded();
            long? rollbackTo = null;
            if (source is CookieSource.Login or CookieSource.Import)
            {
                basedOn = null;
            }
            else
            {
                basedOn ??= generations.FirstOrDefault()?.Number
                    ?? throw new InvalidOperationException($"A {source} generation needs a stored generation to be based on.");
                var parent = generations.Find(g => g.Number == basedOn);
                rollbackTo = source == CookieSource.BrowserRefresh ? parent?.Number : parent?.RollbackTo;
            }

            var now = time.GetUtcNow();
            return Commit(generations, new CookieGeneration(_nextNumber, source, now, basedOn, rollbackTo, knownGood ? now : null, jar));
        }
    }

    /// <summary>Records that <paramref name="number"/> passed verification now.</summary>
    /// <returns>The updated generation, or <see langword="null"/> if it is no longer kept.</returns>
    public CookieGeneration? MarkKnownGood(long number)
    {
        lock (_gate)
        {
            var generations = Loaded();
            var i = generations.FindIndex(g => g.Number == number);
            if (i < 0)
            {
                return null;
            }

            var updated = generations[i] with { KnownGoodAt = time.GetUtcNow() };
            var next = generations.ToList();
            next[i] = updated;
            Save(Prune(next), _nextNumber);
            return updated;
        }
    }

    /// <summary>
    /// Restores <see cref="RollbackTarget"/> as a new <see cref="CookieSource.Rollback"/> generation (step (b) of the
    /// recovery ladder). The new generation is not known-good until verified, and has no rollback target itself.
    /// </summary>
    /// <returns>The new current generation, or <see langword="null"/> when there is nothing to roll back to.</returns>
    public CookieGeneration? RollBack()
    {
        lock (_gate)
        {
            var generations = Loaded();
            if (TargetOf(generations) is not { } target)
            {
                return null;
            }

            return Commit(generations, new CookieGeneration(_nextNumber, CookieSource.Rollback, time.GetUtcNow(), target.Number, null, null, target.Jar));
        }
    }

    /// <summary>
    /// Deletes every generation and the file, and a file set aside as unreadable (log out). Numbering starts over.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (Directory.Exists(paths.AuthDir))
            {
                File.Delete(paths.CookiesBin);
                File.Delete(TempPath);
                File.Delete(UnreadablePath);
            }

            _generations = [];
            _nextNumber = 1;
        }
    }

    private string TempPath => paths.CookiesBin + ".tmp";

    private string UnreadablePath => paths.CookiesBin + ".unreadable";

    private static CookieGeneration? TargetOf(List<CookieGeneration> generations) =>
        generations.FirstOrDefault()?.RollbackTo is { } n ? generations.Find(g => g.Number == n) : null;

    private CookieGeneration Commit(List<CookieGeneration> generations, CookieGeneration generation)
    {
        Save(Prune([generation, .. generations]), generation.Number + 1);
        return generation;
    }

    // Keeps current, its rollback target and the newest known-good ones. Input and output are newest first.
    private static List<CookieGeneration> Prune(List<CookieGeneration> generations)
    {
        if (generations.Count == 0)
        {
            return generations;
        }

        var current = generations[0];
        var keep = new HashSet<long> { current.Number };
        if (current.RollbackTo is { } target)
        {
            keep.Add(target);
        }

        foreach (var g in generations.Skip(1).Where(g => g.IsKnownGood).Take(KnownGoodKept))
        {
            keep.Add(g.Number);
        }

        return generations.Where(g => keep.Contains(g.Number)).ToList();
    }

    // Writes first, then swaps the in-memory state, so a failed write changes nothing.
    private void Save(List<CookieGeneration> generations, long nextNumber)
    {
        var file = new StoreFile(FormatVersion, nextNumber, generations.Select(GenerationDto.From).ToList());
        var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));

        Directory.CreateDirectory(paths.AuthDir);
        File.WriteAllBytes(TempPath, bytes);
        File.Move(TempPath, paths.CookiesBin, overwrite: true);

        _generations = generations;
        _nextNumber = nextNumber;
    }

    // An I/O error while reading propagates and leaves the store unloaded, so the next call tries again rather than
    // overwriting a jar it never read.
    private List<CookieGeneration> Loaded()
    {
        if (_generations is not null)
        {
            return _generations;
        }

        List<CookieGeneration> generations = [];
        long nextNumber = 1;
        if (File.Exists(paths.CookiesBin))
        {
            var bytes = File.ReadAllBytes(paths.CookiesBin);
            try
            {
                var file = JsonSerializer.Deserialize<StoreFile>(protector.Unprotect(bytes), JsonOptions);
                if (file is null || file.Version != FormatVersion)
                {
                    throw new JsonException($"Unsupported cookie store format version {file?.Version}.");
                }

                generations = file.Generations.Select(g => g.ToGeneration()).OrderByDescending(g => g.Number).ToList();
                nextNumber = Math.Max(file.NextNumber, (generations.FirstOrDefault()?.Number ?? 0) + 1);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
            {
                // Another user's or a corrupt file: start logged out, but keep the file for diagnostics.
                File.Move(paths.CookiesBin, UnreadablePath, overwrite: true);
                LogUnreadable(logger, ex, UnreadablePath);
            }
        }

        _generations = generations;
        _nextNumber = nextNumber;
        return generations;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored cookies could not be read; moved them to {Path} and starting logged out")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);

    private sealed record StoreFile(int Version, long NextNumber, List<GenerationDto> Generations);

    private sealed record GenerationDto(
        long Number,
        CookieSource Source,
        DateTimeOffset CreatedAt,
        long? BasedOn,
        long? RollbackTo,
        DateTimeOffset? KnownGoodAt,
        List<CookieDto> Cookies)
    {
        public static GenerationDto From(CookieGeneration g) =>
            new(g.Number, g.Source, g.CreatedAt, g.BasedOn, g.RollbackTo, g.KnownGoodAt, g.Jar.Cookies.Select(CookieDto.From).ToList());

        public CookieGeneration ToGeneration() =>
            new(Number, Source, CreatedAt, BasedOn, RollbackTo, KnownGoodAt, new CookieJar(Cookies.Select(c => c.ToCookie())));
    }

    private sealed record CookieDto(
        string Domain,
        bool IncludeSubdomains,
        string Path,
        bool Secure,
        bool HttpOnly,
        long? Expires,
        string Name,
        string Value)
    {
        public static CookieDto From(JarCookie c) =>
            new(c.Domain, c.IncludeSubdomains, c.Path, c.Secure, c.HttpOnly, c.Expires?.ToUnixTimeSeconds(), c.Name, c.Value);

        public JarCookie ToCookie() => new(Domain, Path, Name, Value)
        {
            IncludeSubdomains = IncludeSubdomains,
            Secure = Secure,
            HttpOnly = HttpOnly,
            Expires = Expires is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : null,
        };
    }
}
