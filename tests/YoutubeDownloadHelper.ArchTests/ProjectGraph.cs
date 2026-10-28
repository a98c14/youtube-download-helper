using System.Text.Json;

namespace YoutubeDownloadHelper.ArchTests;

/// <summary>
/// The dependency graph of the <c>src/</c> projects, read from each project's restore output
/// (<c>obj/project.assets.json</c>). Compiled assembly metadata is not enough: the compiler drops references that
/// nothing uses, so an unused ProjectReference would go unnoticed.
/// </summary>
public sealed class ProjectGraph
{
    private const string SolutionFileName = "YoutubeDownloadHelper.slnx";

    private ProjectGraph(string repoRoot, IReadOnlyDictionary<string, ProjectNode> projects)
    {
        RepoRoot = repoRoot;
        Projects = projects;
    }

    public string RepoRoot { get; }

    /// <summary>The <c>src/</c> projects by name.</summary>
    public IReadOnlyDictionary<string, ProjectNode> Projects { get; }

    public ProjectNode this[string name] => Projects[name];

    public static ProjectGraph Load()
    {
        var repoRoot = FindRepoRoot();
        var direct = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(ReadProject)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        // The assets file doesn't list a referenced project's framework references, so add them here. The project
        // references are already transitive, so one pass is enough.
        var projects = direct.Values
            .Select(p => p with
            {
                FrameworkReferences = p.ProjectReferences
                    .Where(r => direct.ContainsKey(r.Name))
                    .SelectMany(r => direct[r.Name].FrameworkReferences)
                    .Concat(p.FrameworkReferences)
                    .ToHashSet(StringComparer.Ordinal),
            })
            .ToDictionary(p => p.Name, StringComparer.Ordinal);
        return new ProjectGraph(repoRoot, projects);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"{SolutionFileName} not found above {AppContext.BaseDirectory}.");
    }

    private static ProjectNode ReadProject(string csprojPath)
    {
        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            throw new InvalidOperationException($"{assetsPath} is missing. Run \"dotnet restore\" first.");
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var root = doc.RootElement;

        // "libraries" holds every direct and transitive dependency, keyed "Name/Version".
        var projectReferences = new List<ProjectReference>();
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var library in root.GetProperty("libraries").EnumerateObject())
        {
            var name = library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)];
            var type = library.Value.GetProperty("type").GetString();
            if (string.Equals(type, "project", StringComparison.Ordinal))
            {
                var relativePath = library.Value.GetProperty("path").GetString()!;
                projectReferences.Add(new ProjectReference(name, Path.GetFullPath(Path.Combine(projectDir, relativePath))));
            }
            else
            {
                packages.Add(name);
            }
        }

        // Framework references: the project's own, plus those that packages bring along.
        var frameworkReferences = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var framework in root.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("frameworkReferences", out var refs))
            {
                frameworkReferences.UnionWith(refs.EnumerateObject().Select(r => r.Name));
            }
        }

        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("frameworkReferences", out var refs))
                {
                    frameworkReferences.UnionWith(refs.EnumerateArray().Select(r => r.GetString()!));
                }
            }
        }

        return new ProjectNode(
            Path.GetFileNameWithoutExtension(csprojPath),
            projectReferences,
            packages,
            frameworkReferences);
    }
}

public sealed record ProjectNode(
    string Name,
    IReadOnlyList<ProjectReference> ProjectReferences,
    IReadOnlySet<string> Packages,
    IReadOnlySet<string> FrameworkReferences);

public sealed record ProjectReference(string Name, string Path);
