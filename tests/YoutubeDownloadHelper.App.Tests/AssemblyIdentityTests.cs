using System.IO;
using System.Reflection;
using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.App.Tests;

public sealed class AssemblyIdentityTests
{
    private static readonly Assembly App = typeof(App).Assembly;

    [Fact]
    public void App_product_is_the_display_name() =>
        Assert.Equal(AppIdentity.DisplayName, App.GetCustomAttribute<AssemblyProductAttribute>()?.Product);

    [Fact]
    public void App_executable_name_matches_AppIdentity()
    {
        Assert.Equal(AppIdentity.AppExecutableName, App.GetName().Name + ".exe");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, AppIdentity.AppExecutableName)));
    }
}
