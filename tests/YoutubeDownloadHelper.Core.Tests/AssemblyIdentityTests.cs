using System.Reflection;
using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Core.Tests;

public sealed class AssemblyIdentityTests
{
    [Fact]
    public void Contracts_product_is_the_display_name() =>
        Assert.Equal(AppIdentity.DisplayName, ProductOf(typeof(AppIdentity).Assembly));

    [Fact]
    public void Core_product_is_the_display_name() =>
        Assert.Equal(AppIdentity.DisplayName, ProductOf(typeof(CoreAssembly).Assembly));

    private static string? ProductOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
}
