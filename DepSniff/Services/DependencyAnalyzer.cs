using System.Text.Json;
using System.Xml.Linq;
using DepSniff.Models;
using Microsoft.Build.Evaluation;

namespace DepSniff.Services;

public sealed class DependencyAnalyzer
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public List<ProjectInfo> AnalyzeSolution(string slnxPath, Action<string>? onProgress = null)
    {
        var solutionPath = Path.GetFullPath(slnxPath);
        var solutionFolder = Path.GetDirectoryName(solutionPath)!;
        var solution = XDocument.Load(solutionPath);
        var projectPaths = solution.Descendants().Where(element => element.Name.LocalName == "Project").Select(element => (string)element.Attribute("Path")!).Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Select(path => ResolvePath(solutionFolder, path));

        return AnalyzeProjects(projectPaths, onProgress);
    }

    public List<ProjectInfo> AnalyzeProjects(IEnumerable<string> projectPaths, Action<string>? onProgress = null)
    {
        var projects = new List<ProjectInfo>();
        var visited = new HashSet<string>(PathComparer);
        var pending = new Queue<string>(projectPaths.Select(Path.GetFullPath));

        while (pending.TryDequeue(out var projectPath))
        {
            if (visited.Add(projectPath) is false)
            {
                continue;
            }

            onProgress?.Invoke($"Loading project: {Path.GetFileNameWithoutExtension(projectPath)}");
            var project = AnalyzeProject(projectPath);
            projects.Add(project);

            foreach (var reference in project.ProjectReferences)
            {
                pending.Enqueue(reference.Path);
            }
        }

        return projects;
    }

    public static ProjectInfo AnalyzeProject(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        using var collection = new ProjectCollection();
        var project = collection.LoadProject(fullPath);
        var projectFolder = Path.GetDirectoryName(fullPath)!;
        var assetsPath = project.GetPropertyValue("ProjectAssetsFile");
        var info = new ProjectInfo(Path.GetFileNameWithoutExtension(fullPath), fullPath)
        {
            AssetsPath = string.IsNullOrEmpty(assetsPath) ? Path.Combine(projectFolder, "obj", "project.assets.json") : ResolvePath(projectFolder, assetsPath)
        };
        var frameworks = project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries);

        if (frameworks.Length == 0)
        {
            AddProjectItems(project, info);
        }
        else
        {
            foreach (var framework in frameworks)
            {
                var frameworkProject = new Project(fullPath, new Dictionary<string, string> { ["TargetFramework"] = framework }, null, collection);
                AddProjectItems(frameworkProject, info);
            }
        }

        return info;
    }

    private static void AddProjectItems(Project project, ProjectInfo info)
    {
        info.TargetFrameworks.Add(project.GetPropertyValue("TargetFramework"));

        foreach (var item in project.GetItems("PackageReference"))
        {
            var version = item.GetMetadataValue("VersionOverride");

            if (string.IsNullOrEmpty(version))
            {
                version = item.GetMetadataValue("Version");
            }

            if (string.IsNullOrEmpty(version))
            {
                version = project.GetItems("PackageVersion").FirstOrDefault(package => package.EvaluatedInclude.Equals(item.EvaluatedInclude, StringComparison.OrdinalIgnoreCase))?.GetMetadataValue("Version") ?? "Centrally Managed";
            }

            var existingPackageIndex = info.Packages.FindIndex(package => package.Name.Equals(item.EvaluatedInclude, StringComparison.OrdinalIgnoreCase));

            if (existingPackageIndex < 0)
            {
                info.Packages.Add(new PackageReference(item.EvaluatedInclude, version));
            }
            else
            {
                var existingPackage = info.Packages[existingPackageIndex];
                var versions = existingPackage.Version.Split(" / ").Append(version).Distinct(StringComparer.OrdinalIgnoreCase);
                info.Packages[existingPackageIndex] = existingPackage with { Version = string.Join(" / ", versions) };
            }
        }

        foreach (var item in project.GetItems("ProjectReference"))
        {
            var referencePath = ResolvePath(Path.GetDirectoryName(info.Path)!, item.EvaluatedInclude);

            if (info.ProjectReferences.Any(reference => PathComparer.Equals(reference.Path, referencePath)) is false)
            {
                info.ProjectReferences.Add(new ProjectReferenceInfo(referencePath));
            }
        }
    }

    public static List<RedundancyInfo> FindRedundantPackages(ProjectInfo project)
    {
        using var assets = ReadAssets(project);

        if (assets is null)
        {
            return [];
        }

        var targets = assets.RootElement.GetProperty("targets");
        var redundant = new List<RedundancyInfo>();

        foreach (var package in project.Packages)
        {
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var applicableTargets = 0;
            var redundantInEveryTarget = true;

            foreach (var target in targets.EnumerateObject())
            {
                var libraries = target.Value.EnumerateObject().ToDictionary(library => library.Name.Split('/')[0], library => library.Value, StringComparer.OrdinalIgnoreCase);

                if (libraries.ContainsKey(package.Name) is false)
                {
                    continue;
                }

                applicableTargets++;
                var roots = GetDirectRoots(project, assets.RootElement, target.Name, libraries);
                var source = roots.FirstOrDefault(root => root.Equals(package.Name, StringComparison.OrdinalIgnoreCase) is false && HasPackageDependency(package.Name, root, libraries));

                if (source is null)
                {
                    redundantInEveryTarget = false;
                    break;
                }

                sources.Add(source);
            }

            if (applicableTargets > 0 && redundantInEveryTarget)
            {
                redundant.Add(new RedundancyInfo(package.Name, string.Join(", ", sources.Order(StringComparer.OrdinalIgnoreCase))));
            }
        }

        return redundant;
    }

    private static IEnumerable<string> GetDirectRoots(ProjectInfo project, JsonElement assets, string targetName, Dictionary<string, JsonElement> libraries)
    {
        var framework = targetName.Split('/')[0];
        var packageNames = project.Packages.Select(package => package.Name);

        if (assets.TryGetProperty("project", out var restoredProject) && restoredProject.TryGetProperty("frameworks", out var frameworks) && frameworks.TryGetProperty(framework, out var targetFramework))
        {
            var restoredPackageNames = targetFramework.TryGetProperty("dependencies", out var dependencies) ? dependencies.EnumerateObject().Select(dependency => dependency.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            packageNames = packageNames.Where(restoredPackageNames.Contains);
        }

        var roots = packageNames.Where(libraries.ContainsKey).ToList();

        if (assets.TryGetProperty("libraries", out var assetLibraries))
        {
            foreach (var library in assetLibraries.EnumerateObject())
            {
                if (library.Value.TryGetProperty("msbuildProject", out var projectPath))
                {
                    var fullPath = ResolvePath(Path.GetDirectoryName(project.Path)!, projectPath.GetString()!);
                    var libraryName = library.Name.Split('/')[0];

                    if (libraries.ContainsKey(libraryName) && project.ProjectReferences.Any(reference => PathComparer.Equals(reference.Path, fullPath)))
                    {
                        roots.Add(libraryName);
                    }
                }
            }
        }

        return roots;
    }

    private static bool HasPackageDependency(string targetPackage, string parent, Dictionary<string, JsonElement> libraries)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(parent);

        while (pending.TryPop(out var current))
        {
            if (visited.Add(current) is false || libraries.TryGetValue(current, out var library) is false || library.TryGetProperty("dependencies", out var dependencies) is false)
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (dependency.Name.Equals(targetPackage, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                pending.Push(dependency.Name);
            }
        }

        return false;
    }

    public List<RedundancyInfo> FindRedundantReferences(ProjectInfo project, List<ProjectInfo> allProjects)
    {
        var projectsByPath = allProjects.ToDictionary(candidate => candidate.Path, PathComparer);
        var redundant = new List<RedundancyInfo>();

        foreach (var reference in project.ProjectReferences)
        {
            foreach (var other in project.ProjectReferences.Where(candidate => PathComparer.Equals(candidate.Path, reference.Path) is false))
            {
                if (HasProjectDependency(reference.Path, other.Path, projectsByPath))
                {
                    redundant.Add(new RedundancyInfo(reference.Path, Path.GetFileNameWithoutExtension(other.Path)));
                    break;
                }
            }
        }

        return redundant;
    }

    private static bool HasProjectDependency(string targetPath, string parentPath, Dictionary<string, ProjectInfo> projects)
    {
        var visited = new HashSet<string>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(parentPath);

        while (pending.TryPop(out var current))
        {
            if (visited.Add(current) is false || projects.TryGetValue(current, out var project) is false)
            {
                continue;
            }

            foreach (var reference in project.ProjectReferences)
            {
                if (PathComparer.Equals(reference.Path, targetPath))
                {
                    return true;
                }

                pending.Push(reference.Path);
            }
        }

        return false;
    }

    internal static string GetRestoredFrameworkName(JsonElement assets, string framework)
    {
        if (assets.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks))
        {
            foreach (var restoredFramework in frameworks.EnumerateObject())
            {
                if (restoredFramework.Name.Equals(framework, StringComparison.OrdinalIgnoreCase) || restoredFramework.Value.TryGetProperty("targetAlias", out var alias) && string.Equals(alias.GetString(), framework, StringComparison.OrdinalIgnoreCase))
                {
                    return restoredFramework.Name;
                }
            }
        }

        return framework;
    }

    internal static JsonDocument? ReadAssets(ProjectInfo project)
    {
        try
        {
            using var stream = File.OpenRead(project.AssetsPath);
            var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind is not JsonValueKind.Object || document.RootElement.TryGetProperty("targets", out var targets) is false || targets.ValueKind is not JsonValueKind.Object)
            {
                document.Dispose();
                throw new JsonException("The assets file does not contain dependency targets.");
            }

            return document;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            var warning = $"Cannot read restored dependencies for {project.Name}: {exception.Message} Run dotnet restore before analyzing.";

            if (project.Warnings.Contains(warning) is false)
            {
                project.Warnings.Add(warning);
            }

            return null;
        }
    }

    private static string ResolvePath(string folder, string path) => Path.GetFullPath(Path.Combine(folder, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    public sealed record RedundancyInfo(string Name, string Source);
}
