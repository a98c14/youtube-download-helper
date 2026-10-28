using System.Reflection;
using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Client.Tests;

public sealed class AssemblyIdentityTests
{
    [Fact]
    public void Client_product_is_the_display_name() =>
        Assert.Equal(
            AppIdentity.DisplayName,
            typeof(ClientAssembly).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
}
