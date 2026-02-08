using System.Text.Json;
using DepSniff.Models;
using Microsoft.Build.Evaluation;

namespace DepSniff.Services;

public class DependencyAnalyzer
{
    public List<ProjectInfo> AnalyzeSolution(string slnxPath, Action<string>? onProgress = null)
    {
        var projects = new List<ProjectInfo>();
        var solutionFolder = Path.GetDirectoryName(slnxPath) ?? "";
        var content = File.ReadAllLines(slnxPath);

        foreach (var line in content)
        {
            if (line.Contains("<Project Path=\""))
            {
                var start = line.IndexOf("Path=\"") + 6;
                var end = line.IndexOf("\"", start);
                var projectRelativePath = line.Substring(start, end - start);
                var projectFullPath = Path.GetFullPath(Path.Combine(solutionFolder, projectRelativePath));

                if (File.Exists(projectFullPath))
                {
                    onProgress?.Invoke($"Loading project: {Path.GetFileNameWithoutExtension(projectFullPath)}");
                    projects.Add(AnalyzeProject(projectFullPath));
                }
            }
        }

        // After all projects are loaded, analyze transitive dependencies
        onProgress?.Invoke("Analyzing transitive dependencies...");
        AnalyzeTransitiveDependencies(projects, onProgress);
        
        onProgress?.Invoke("Resolving project references...");
        ResolveProjectReferencesInAssets(projects);

        return projects;
    }

    private static void ResolveProjectReferencesInAssets(List<ProjectInfo> allProjects)
    {
        foreach (var project in allProjects)
        {
            // We need to know which packages come from which projects
            foreach (var pr in project.ProjectReferences)
            {
                var referencedProject = allProjects.FirstOrDefault(p => p.Path == pr.Path);
                if (referencedProject != null)
                {
                    foreach (var pkg in referencedProject.Packages)
                    {
                        if (!project.Packages.Any(p => p.Name.Equals(pkg.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            project.Packages.Add(pkg with { IsTransitive = true, Source = referencedProject.Name });
                        }
                    }
                }
            }
        }
    }

    public static ProjectInfo AnalyzeProject(string projectPath)
    {
        var project = ProjectCollection.GlobalProjectCollection.LoadProject(projectPath);
        var info = new ProjectInfo(Path.GetFileNameWithoutExtension(projectPath), projectPath);

        var packageReferences = project.GetItems("PackageReference");
        foreach (var item in packageReferences)
        {
            var name = item.EvaluatedInclude;
            var version = item.GetMetadataValue("Version");

            if (string.IsNullOrEmpty(version))
            {
                version = item.GetMetadataValue("VersionOverride");
                if (string.IsNullOrEmpty(version))
                {
                    version = "Centrally Managed";
                }
            }

            info.Packages.Add(new PackageReference(name, version));
        }

        var projectReferences = project.GetItems("ProjectReference");
        foreach (var item in projectReferences)
        {
            info.ProjectReferences.Add(new ProjectReferenceInfo(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!,
                item.EvaluatedInclude))));
        }

        ProjectCollection.GlobalProjectCollection.UnloadProject(project);
        return info;
    }

    private static void AnalyzeTransitiveDependencies(List<ProjectInfo> projects, Action<string>? onProgress = null)
    {
        foreach (var project in projects)
        {
            onProgress?.Invoke($"Analyzing transitive dependencies for {project.Name}...");
            var assetsPath = Path.Combine(Path.GetDirectoryName(project.Path)!, "obj", "project.assets.json");
            if (File.Exists(assetsPath))
            {
                try
                {
                    using var stream = File.OpenRead(assetsPath);
                    var doc = JsonDocument.Parse(stream);

                    if (doc.RootElement.TryGetProperty("targets", out var targets))
                    {
                        foreach (var target in targets.EnumerateObject())
                        {
                            foreach (var lib in target.Value.EnumerateObject())
                            {
                                var parts = lib.Name.Split('/');
                                var name = parts[0];

                                // If this library is one of our DIRECT references, check its dependencies
                                var directPkg = project.Packages.FirstOrDefault(p =>
                                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !p.IsTransitive);

                                if (directPkg != null && lib.Value.TryGetProperty("dependencies", out var deps))
                                {
                                    foreach (var dep in deps.EnumerateObject())
                                    {
                                        var depName = dep.Name;
                                        var depVersion = dep.Value.GetString() ?? "";

                                        // Add as transitive package
                                        if (!project.Packages.Any(p =>
                                                p.Name.Equals(depName, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            project.Packages.Add(new PackageReference(depName, depVersion,
                                                IsTransitive: true, Source: directPkg.Name));
                                        }
                                        else if (project.Packages.Any(p =>
                                                     p.Name.Equals(depName, StringComparison.OrdinalIgnoreCase) &&
                                                     !p.IsTransitive))
                                        {
                                            // It already exists as direct, but we don't mark it here, FindRedundantPackages handles it
                                        }
                                    }
                                }

                                // Check if it's a project we reference
                                var referencedProject = project.ProjectReferences.FirstOrDefault(pr =>
                                    Path.GetFileNameWithoutExtension(pr.Path)
                                        .Equals(name, StringComparison.OrdinalIgnoreCase));
                                if (referencedProject != null &&
                                    lib.Value.TryGetProperty("dependencies", out var projectDeps))
                                {
                                    foreach (var dep in projectDeps.EnumerateObject())
                                    {
                                        var depName = dep.Name;
                                        var depVersion = dep.Value.GetString() ?? "";

                                        // If the project we reference has a package, we get it transitively
                                        if (!project.Packages.Any(p =>
                                                p.Name.Equals(depName, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            project.Packages.Add(new PackageReference(depName, depVersion,
                                                IsTransitive: true, Source: name));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore errors when reading assets
                }
            }
        }
    }

    public static List<RedundancyInfo> FindRedundantPackages(ProjectInfo project)
    {
        var redundant = new List<RedundancyInfo>();

        var assetsPath = Path.Combine(Path.GetDirectoryName(project.Path)!, "obj", "project.assets.json");
        if (!File.Exists(assetsPath)) return redundant;

        try
        {
            using var stream = File.OpenRead(assetsPath);
            var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("targets", out var targets)) return redundant;

            // We only care about direct packages for redundancy check
            var directPackages = project.Packages.Where(p => !p.IsTransitive).ToList();

            foreach (var direct in directPackages)
            {
                foreach (var target in targets.EnumerateObject())
                {
                    // Check other direct packages for this direct package
                    foreach (var otherDirect in directPackages.Where(p => p.Name != direct.Name))
                    {
                        var chain = GetPackageDependencyChain(direct.Name, otherDirect.Name, target.Value);
                        if (chain != null)
                        {
                            redundant.Add(new RedundancyInfo(direct.Name, otherDirect.Name));
                            goto NextDirect; // Found a redundancy for this one
                        }
                    }

                    // Check project references
                    foreach (var pr in project.ProjectReferences)
                    {
                        var prName = Path.GetFileNameWithoutExtension(pr.Path);
                        var chain = GetPackageDependencyChain(direct.Name, prName, target.Value);
                        if (chain != null)
                        {
                            redundant.Add(new RedundancyInfo(direct.Name, prName));
                            goto NextDirect;
                        }
                    }
                }
                NextDirect:;
            }
        }
        catch
        {
            // Fallback to simple check if assets parsing fails
            var directPackages = project.Packages.Where(p => !p.IsTransitive).ToList();
            var transitivePackages = project.Packages.Where(p => p.IsTransitive).ToList();

            foreach (var direct in directPackages)
            {
                var transitive = transitivePackages.FirstOrDefault(t => t.Name.Equals(direct.Name, StringComparison.OrdinalIgnoreCase));
                if (transitive != null)
                {
                    redundant.Add(new RedundancyInfo(direct.Name, transitive.Source ?? "Unknown"));
                }
            }
        }

        return redundant;
    }

    private static List<string>? GetPackageDependencyChain(string targetPackage, string currentParent, JsonElement target)
    {
        // Find the currentParent in target libraries
        foreach (var lib in target.EnumerateObject())
        {
            var libName = lib.Name.Split('/')[0];
            if (libName.Equals(currentParent, StringComparison.OrdinalIgnoreCase))
            {
                if (lib.Value.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        var depName = dep.Name;
                        if (depName.Equals(targetPackage, StringComparison.OrdinalIgnoreCase))
                        {
                            return [depName];
                        }

                        var chain = GetPackageDependencyChain(targetPackage, depName, target);
                        if (chain != null)
                        {
                            chain.Insert(0, depName);
                            return chain;
                        }
                    }
                }
            }
        }

        return null;
    }

    public List<RedundancyInfo> FindRedundantReferences(ProjectInfo project, List<ProjectInfo> allProjects)
    {
        var redundant = new List<RedundancyInfo>();

        // 1. Check if a project reference is already obtained through another project reference
        foreach (var pr in project.ProjectReferences)
        {
            var otherProjects = project.ProjectReferences.Where(p => p != pr)
                .Select(p => allProjects.FirstOrDefault(ap => ap.Path == p.Path))
                .Where(ap => ap != null);

            foreach (var other in otherProjects)
            {
                var chain = GetProjectDependencyChain(pr.Path, other!, allProjects);
                if (chain != null)
                {
                    redundant.Add(new RedundancyInfo(pr.Path, other!.Name));
                    break;
                }
            }
        }

        return redundant;
    }

    public record RedundancyInfo(string Name, string Source);

    private static List<string>? GetProjectDependencyChain(string targetPath, ProjectInfo current, List<ProjectInfo> allProjects)
    {
        if (current.ProjectReferences.Any(pr => pr.Path == targetPath))
        {
            return [Path.GetFileNameWithoutExtension(targetPath)];
        }

        foreach (var pr in current.ProjectReferences)
        {
            var next = allProjects.FirstOrDefault(p => p.Path == pr.Path);
            if (next != null)
            {
                var chain = GetProjectDependencyChain(targetPath, next, allProjects);
                if (chain != null)
                {
                    chain.Insert(0, next.Name);
                    return chain;
                }
            }
        }

        return null;
    }

    public void AnalyzeUsage(ProjectInfo project, Action<string>? onProgress = null)
    {
        onProgress?.Invoke($"Analyzing usage for {project.Name}...");

        var projectDir = Path.GetDirectoryName(project.Path)!;
        var csFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        if (csFiles.Count == 0) return;

        var fileContents = csFiles.Select(File.ReadAllText).ToList();

        // Check NuGet packages
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        var packageAssemblies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(assetsPath))
        {
            try
            {
                using var stream = File.OpenRead(assetsPath);
                var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("targets", out var targets))
                {
                    foreach (var target in targets.EnumerateObject())
                    {
                        foreach (var lib in target.Value.EnumerateObject())
                        {
                            var parts = lib.Name.Split('/');
                            var name = parts[0];

                            if (lib.Value.TryGetProperty("compile", out var compile))
                            {
                                var dlls = new List<string>();
                                foreach (var dll in compile.EnumerateObject())
                                {
                                    var dllName = Path.GetFileNameWithoutExtension(dll.Name);
                                    if (!string.IsNullOrEmpty(dllName) && dllName != "_._")
                                    {
                                        dlls.Add(dllName);
                                    }
                                }

                                if (dlls.Count > 0)
                                {
                                    if (packageAssemblies.TryGetValue(name, out var existing))
                                    {
                                        existing.AddRange(dlls);
                                    }
                                    else
                                    {
                                        packageAssemblies[name] = dlls;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore assets parsing errors
            }
        }

        foreach (var pkg in project.Packages.Where(p => !p.IsTransitive))
        {
            // Try to find if any assembly from this package is used
            var searchStrings = new List<string> { pkg.Name };
            if (packageAssemblies.TryGetValue(pkg.Name, out var dlls))
            {
                searchStrings.AddRange(dlls);
            }

            searchStrings = searchStrings.Distinct().ToList();

            var isUsed = false;
            foreach (var content in fileContents)
            {
                foreach (var s in searchStrings)
                {
                    // Simple check: does the assembly name appear as a word?
                    // This catches 'using Spectre.Console;' and 'Spectre.Console.AnsiConsole'
                    if (content.Contains(s))
                    {
                        isUsed = true;
                        break;
                    }
                }
                if (isUsed) break;
            }

            pkg.IsUsed = isUsed;
        }

        // Check Project references
        foreach (var pr in project.ProjectReferences)
        {
            var prName = Path.GetFileNameWithoutExtension(pr.Path);
            var isUsed = false;
            foreach (var content in fileContents)
            {
                if (content.Contains(prName))
                {
                    isUsed = true;
                    break;
                }
            }
            pr.IsUsed = isUsed;
        }
    }
}
