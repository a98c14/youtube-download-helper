namespace YoutubeDownloadHelper.ArchTests;

/// <summary>Dependency rules from PLAN.md §4.1 and §4.2, checked against the restore graph (direct and transitive).</summary>
public sealed class DependencyRuleTests
{
    private const string Contracts = "YoutubeDownloadHelper.Contracts";
    private const string Core = "YoutubeDownloadHelper.Core";
    private const string Engine = "YoutubeDownloadHelper.Engine";
    private const string Client = "YoutubeDownloadHelper.Client";
    private const string App = "YoutubeDownloadHelper.App";

    private const string AspNetCorePrefix = "Microsoft.AspNetCore.";
    private const string WindowsDesktopFramework = "Microsoft.WindowsDesktop.App";

    private static readonly ProjectGraph Graph = ProjectGraph.Load();

    [Fact]
    public void All_src_projects_are_found() =>
        Assert.Equal([App, Client, Contracts, Core, Engine], Graph.Projects.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(App, Core)]
    [InlineData(App, Engine)]
    [InlineData(Client, Core)]
    [InlineData(Client, Engine)]
    public void Project_does_not_reference(string project, string forbidden) =>
        Assert.DoesNotContain(forbidden, Graph[project].ProjectReferences.Select(r => r.Name));

    [Fact]
    public void Core_does_not_use_AspNetCore()
    {
        var core = Graph[Core];
        Assert.DoesNotContain(core.Packages, p => p.StartsWith(AspNetCorePrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(core.FrameworkReferences, p => p.StartsWith(AspNetCorePrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Core)]
    [InlineData(Engine)]
    public void Project_does_not_use_WPF_or_WinForms(string project) =>
        Assert.DoesNotContain(
            Graph[project].FrameworkReferences,
            f => f.StartsWith(WindowsDesktopFramework, StringComparison.Ordinal));

    [Fact]
    public void Contracts_has_no_dependencies()
    {
        var contracts = Graph[Contracts];
        Assert.Empty(contracts.ProjectReferences);
        Assert.Empty(contracts.Packages);
    }

    [Fact]
    public void Nothing_in_src_references_Engine() =>
        Assert.All(
            Graph.Projects.Values,
            p => Assert.DoesNotContain(Engine, p.ProjectReferences.Select(r => r.Name)));

    [Fact]
    public void Src_projects_reference_only_src_projects()
    {
        var srcDir = Path.Combine(Graph.RepoRoot, "src") + Path.DirectorySeparatorChar;
        Assert.All(
            Graph.Projects.Values.SelectMany(p => p.ProjectReferences, (p, r) => (Project: p.Name, Reference: r)),
            x => Assert.True(
                x.Reference.Path.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase),
                $"{x.Project} references {x.Reference.Path}, which is outside src/."));
    }
}
