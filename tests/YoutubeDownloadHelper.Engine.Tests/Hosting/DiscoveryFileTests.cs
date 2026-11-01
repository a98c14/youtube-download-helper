using System.Security.AccessControl;
using System.Security.Principal;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class DiscoveryFileTests
{
    [Fact]
    public async Task Written_once_listening_with_the_engine_details()
    {
        await using var engine = await TestEngine.StartAsync();

        var discovery = DiscoveryFile.TryRead(engine.Paths.EngineJson);

        Assert.NotNull(discovery);
        Assert.Equal(
            new EngineDiscovery(ApiRoutes.Version, "http://127.0.0.1:47815", engine.Token.Value, Environment.ProcessId, EngineVersion.Current),
            discovery);
        Assert.Equal(43, discovery.Token.Length); // 256 bits, base64url without padding
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+", discovery.EngineVersion);
        Assert.DoesNotContain("+", discovery.EngineVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_uses_the_documented_camelCase_names()
    {
        await using var engine = await TestEngine.StartAsync();

        var json = await File.ReadAllTextAsync(engine.Paths.EngineJson, TestContext.Current.CancellationToken);

        foreach (var name in (string[])["\"apiVersion\": 1", "\"baseUrl\": \"http://127.0.0.1:47815\"", "\"token\":", "\"pid\":", "\"engineVersion\":"])
        {
            Assert.Contains(name, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_the_current_user_can_access_it()
    {
        await using var engine = await TestEngine.StartAsync();

        AssertUserOnly(engine.Paths.EngineJson);
    }

    [Fact]
    public async Task Deleted_on_clean_shutdown()
    {
        await using var engine = await TestEngine.StartAsync();
        Assert.True(File.Exists(engine.Paths.EngineJson));

        await engine.StopAsync();

        Assert.False(File.Exists(engine.Paths.EngineJson));
        Assert.Empty(Directory.GetFiles(engine.Paths.Root, "engine.json*"));
    }

    [Fact]
    public async Task A_stale_file_is_replaced()
    {
        var paths = TestEngine.NewDataPaths();
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.EngineJson, "{ \"stale\": true }", TestContext.Current.CancellationToken);

        await using var engine = await TestEngine.StartAsync(paths);

        Assert.Equal(engine.Token.Value, DiscoveryFile.TryRead(paths.EngineJson)?.Token);
        AssertUserOnly(paths.EngineJson);
        Assert.Single(Directory.GetFiles(paths.Root, "engine.json*"));
    }

    [Fact]
    public async Task Another_engines_file_is_left_alone_on_shutdown()
    {
        await using var engine = await TestEngine.StartAsync();
        var newer = new EngineDiscovery(ApiRoutes.Version, "http://127.0.0.1:1", new ApiToken().Value, 1, "9.9.9");
        DiscoveryFile.Write(engine.Paths.EngineJson, newer);

        await engine.StopAsync();

        Assert.Equal(newer, DiscoveryFile.TryRead(engine.Paths.EngineJson));
    }

    [Fact]
    public void Write_is_atomic_and_leaves_no_temporary_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", $"İ {Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, EngineDiscovery.FileName);
            var first = new EngineDiscovery(1, "http://127.0.0.1:1", "a", 1, "1.0.0");
            var second = first with { Token = "b" };

            DiscoveryFile.Write(path, first);
            DiscoveryFile.Write(path, second);

            Assert.Equal(second, DiscoveryFile.TryRead(path));
            Assert.Equal([path], Directory.GetFiles(dir));
            AssertUserOnly(path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryRead_returns_null_for_a_missing_or_broken_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", $"{Guid.NewGuid():N}.json");
        Assert.Null(DiscoveryFile.TryRead(path));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Null(DiscoveryFile.TryRead(path));
            Assert.False(DiscoveryFile.DeleteIfOwned(path, "token"));
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static void AssertUserOnly(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileInfo(path).GetAccessControl();

        Assert.True(security.AreAccessRulesProtected, "The file inherits access rules from its folder.");
        var rule = Assert.Single(security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }
}
