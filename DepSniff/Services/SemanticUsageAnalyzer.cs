using System.Text.Json;
using DepSniff.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using ProjectInfo = DepSniff.Models.ProjectInfo;

namespace DepSniff.Services;

public sealed class SemanticUsageAnalyzer
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task AnalyzeAsync(ProjectInfo project, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        foreach (var package in project.Packages)
        {
            package.Usage = DependencyUsage.Unknown;
        }

        foreach (var reference in project.ProjectReferences)
        {
            reference.Usage = DependencyUsage.Unknown;
        }

        using var assets = DependencyAnalyzer.ReadAssets(project);

        if (assets is null)
        {
            return;
        }

        var packageResults = project.Packages.ToDictionary(package => package.Name, _ => new List<DependencyUsage>(), StringComparer.OrdinalIgnoreCase);
        var referenceResults = project.ProjectReferences.ToDictionary(reference => reference.Path, _ => new List<DependencyUsage>(), PathComparer);
        var frameworks = project.TargetFrameworks.Count > 0 ? project.TargetFrameworks : [""];

        foreach (var framework in frameworks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Analyzing symbols for {project.Name} ({framework})...");

            try
            {
                await AnalyzeFrameworkAsync(project, framework, assets.RootElement, packageResults, referenceResults, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                project.Warnings.Add($"Semantic analysis failed for {project.Name} ({framework}): {exception.Message}");
                AddUnknownResults(packageResults, referenceResults);
            }
        }

        foreach (var package in project.Packages)
        {
            package.Usage = CombineResults(packageResults[package.Name]);
        }

        foreach (var reference in project.ProjectReferences)
        {
            reference.Usage = CombineResults(referenceResults[reference.Path]);
        }
    }

    private static async Task AnalyzeFrameworkAsync(ProjectInfo project, string framework, JsonElement assets, Dictionary<string, List<DependencyUsage>> packageResults, Dictionary<string, List<DependencyUsage>> referenceResults, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(framework) is false)
        {
            properties["TargetFramework"] = framework;
        }

        using var workspace = MSBuildWorkspace.Create(properties);
        var loadedProject = await workspace.OpenProjectAsync(project.Path, cancellationToken: cancellationToken);
        var compilation = await loadedProject.GetCompilationAsync(cancellationToken);
        var failures = workspace.Diagnostics.Where(diagnostic => diagnostic.Kind is WorkspaceDiagnosticKind.Failure).Select(diagnostic => diagnostic.Message).ToList();

        if (compilation is null)
        {
            failures.Add("No C# compilation was available.");
        }
        else
        {
            failures.AddRange(compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error).Take(3).Select(diagnostic => diagnostic.ToString()));
        }

        if (failures.Count > 0)
        {
            project.Warnings.Add($"Semantic analysis is incomplete for {project.Name} ({framework}): {string.Join(" ", failures)}");
            AddUnknownResults(packageResults, referenceResults);

            return;
        }

        var usedReferences = compilation!.GetUsedAssemblyReferences(cancellationToken).ToHashSet();
        var referencesByPath = compilation.References.OfType<PortableExecutableReference>().Where(reference => reference.FilePath is not null).ToDictionary(reference => Path.GetFullPath(reference.FilePath!), PathComparer);
        var targetFramework = string.IsNullOrEmpty(framework) ? assets.GetProperty("targets").EnumerateObject().FirstOrDefault().Name : DependencyAnalyzer.GetRestoredFrameworkName(assets, framework);

        if (assets.GetProperty("targets").TryGetProperty(targetFramework ?? "", out var target) is false)
        {
            project.Warnings.Add($"No restored dependency target was found for {project.Name} ({framework}). Run dotnet restore.");
            AddUnknownResults(packageResults, referenceResults);

            return;
        }

        foreach (var library in target.EnumerateObject())
        {
            var name = library.Name.Split('/')[0];

            if (packageResults.TryGetValue(name, out var results) is false)
            {
                continue;
            }

            if (library.Value.TryGetProperty("compile", out var compile) is false || compile.EnumerateObject().Any(assembly => assembly.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) is false)
            {
                results.Add(DependencyUsage.NoCompileAssets);
                continue;
            }

            var paths = GetPackageAssemblyPaths(assets, library).ToList();
            var packageReferences = paths.Where(referencesByPath.ContainsKey).Select(path => referencesByPath[path]).Distinct().ToList();
            var compileAssemblyCount = compile.EnumerateObject().Count(assembly => assembly.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            results.Add(packageReferences.Any(usedReferences.Contains) ? DependencyUsage.Used : packageReferences.Count == compileAssemblyCount ? DependencyUsage.NotUsed : DependencyUsage.Unknown);
        }

        foreach (var reference in loadedProject.ProjectReferences)
        {
            var referencedProject = loadedProject.Solution.GetProject(reference.ProjectId)!;

            if (referencedProject.FilePath is null || referenceResults.TryGetValue(Path.GetFullPath(referencedProject.FilePath), out var results) is false)
            {
                continue;
            }

            var referencedCompilation = await referencedProject.GetCompilationAsync(cancellationToken);

            if (referencedCompilation is null)
            {
                results.Add(DependencyUsage.Unknown);
                continue;
            }

            var matchingReferences = compilation.References.Where(candidate => compilation.GetAssemblyOrModuleSymbol(candidate) is IAssemblySymbol assembly && assembly.Identity.Equals(referencedCompilation.Assembly.Identity)).ToList();
            results.Add(matchingReferences.Any(usedReferences.Contains) ? DependencyUsage.Used : matchingReferences.Count > 0 ? DependencyUsage.NotUsed : DependencyUsage.Unknown);
        }
    }

    private static IEnumerable<string> GetPackageAssemblyPaths(JsonElement assets, JsonProperty library)
    {
        if (library.Value.TryGetProperty("compile", out var compile) is false || assets.TryGetProperty("packageFolders", out var folders) is false || assets.TryGetProperty("libraries", out var libraries) is false || libraries.TryGetProperty(library.Name, out var package) is false || package.TryGetProperty("path", out var packagePath) is false)
        {
            yield break;
        }

        foreach (var assembly in compile.EnumerateObject())
        {
            if (assembly.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var folder in folders.EnumerateObject())
                {
                    yield return Path.GetFullPath(Path.Combine(folder.Name, packagePath.GetString()!, assembly.Name).Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }
    }

    private static void AddUnknownResults(Dictionary<string, List<DependencyUsage>> packages, Dictionary<string, List<DependencyUsage>> references)
    {
        foreach (var results in packages.Values.Concat(references.Values))
        {
            results.Add(DependencyUsage.Unknown);
        }
    }

    private static DependencyUsage CombineResults(List<DependencyUsage> results)
    {
        if (results.Contains(DependencyUsage.Used))
        {
            return DependencyUsage.Used;
        }

        if (results.Count == 0 || results.Contains(DependencyUsage.Unknown))
        {
            return DependencyUsage.Unknown;
        }

        return results.Contains(DependencyUsage.NotUsed) ? DependencyUsage.NotUsed : DependencyUsage.NoCompileAssets;
    }
}
