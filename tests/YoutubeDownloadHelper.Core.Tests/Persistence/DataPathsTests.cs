using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Persistence;

public sealed class DataPathsTests
{
    [Fact]
    public void Default_root_is_outside_the_Velopack_install_root()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = DataPaths.ForCurrentUser().Root;

        Assert.Equal(Path.Combine(localAppData, AppIdentity.DataFolderName), root);
        Assert.False(
            (root + Path.DirectorySeparatorChar).StartsWith(
                Path.Combine(localAppData, AppIdentity.VelopackPackId) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Layout_matches_the_plan()
    {
        var paths = new DataPaths(@"C:\Data İı");

        Assert.Equal(@"C:\Data İı\state.db", paths.StateDb);
        Assert.Equal(@"C:\Data İı\engine.json", paths.EngineJson);
        Assert.Equal(@"C:\Data İı\auth\cookies.bin", paths.CookiesBin);
        Assert.Equal(@"C:\Data İı\webview-profile", paths.WebViewProfileDir);
        Assert.Equal(@"C:\Data İı\tools", paths.ToolsDir);
        Assert.Equal(@"C:\Data İı\work\1234567", paths.WorkDirFor(1_234_567));
        Assert.Equal(@"C:\Data İı\logs\jobs", paths.JobLogsDir);
        Assert.Equal(@"C:\Data İı\backups", paths.BackupsDir);
        Assert.Equal(@"C:\Data İı\cache\yt-dlp", paths.YtDlpCacheDir);
    }

    [Fact]
    public void Relative_roots_are_made_absolute() =>
        Assert.True(Path.IsPathFullyQualified(new DataPaths("data").Root));
}
