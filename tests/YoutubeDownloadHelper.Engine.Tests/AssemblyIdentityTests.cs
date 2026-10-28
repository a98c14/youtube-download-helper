using System.Reflection;
using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Engine.Tests;

public sealed class AssemblyIdentityTests
{
    private static readonly Assembly Engine = typeof(Program).Assembly;

    [Fact]
    public void Engine_product_is_the_display_name() =>
        Assert.Equal(AppIdentity.DisplayName, Engine.GetCustomAttribute<AssemblyProductAttribute>()?.Product);

    [Fact]
    public void Engine_executable_name_matches_AppIdentity()
    {
        Assert.Equal(AppIdentity.EngineExecutableName, Engine.GetName().Name + ".exe");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, AppIdentity.EngineExecutableName)));
    }
}
