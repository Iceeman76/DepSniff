using System.Text.Json;
using DepSniff.Models;
using DepSniff.Services;
using NUnit.Framework;

namespace DepSniff.Tests;

[TestFixture]
public sealed class DependencyAnalyzerTests
{
    private string projectFolder = null!;
    private ProjectInfo project = null!;
    private readonly DependencyAnalyzer analyzer = new();

    [SetUp]
    public void SetUp()
    {
        projectFolder = Path.Combine(Path.GetTempPath(), "DepSniff.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectFolder, "obj"));
        project = new ProjectInfo("App", Path.Combine(projectFolder, "App.csproj"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(projectFolder, true);

    [Test]
    public void FindRedundantPackagesFindsDeepTransitiveDependenciesIgnoringCase()
    {
        project.Packages.AddRange([new PackageReference("Parent", "1.0.0"), new PackageReference("Child", "1.0.0")]);
        WriteTargets(("net10.0", """
            { "Parent/1.0.0": { "dependencies": { "Intermediate": "1.0.0" } }, "Intermediate/1.0.0": { "dependencies": { "child": "1.0.0" } }, "Child/1.0.0": {} }
            """));
        var result = DependencyAnalyzer.FindRedundantPackages(project);

        Assert.That(result, Is.EqualTo(new[] { new DependencyAnalyzer.RedundancyInfo("Child", "Parent") }));
    }

    [Test]
    public void FindRedundantPackagesTerminatesForCyclicDependencies()
    {
        project.Packages.AddRange([new PackageReference("Parent", "1.0.0"), new PackageReference("Unrelated", "1.0.0")]);
        WriteTargets(("net10.0", """
            { "Parent/1.0.0": { "dependencies": { "Other": "1.0.0" } }, "Other/1.0.0": { "dependencies": { "Parent": "1.0.0" } }, "Unrelated/1.0.0": {} }
            """));

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project), Is.Empty);
    }

    [Test]
    public void FindRedundantPackagesRequiresTransitiveDependencyInEveryTarget()
    {
        project.Packages.AddRange([new PackageReference("Parent", "1.0.0"), new PackageReference("Child", "1.0.0")]);
        WriteTargets(("net9.0", """{ "Parent/1.0.0": { "dependencies": { "Child": "1.0.0" } }, "Child/1.0.0": {} }"""), ("net10.0", """{ "Parent/1.0.0": {}, "Child/1.0.0": {} }"""));

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project), Is.Empty);
    }

    [Test]
    public void FindRedundantPackagesAcceptsTransitiveDependencyInEveryTarget()
    {
        project.Packages.AddRange([new PackageReference("Parent", "1.0.0"), new PackageReference("Child", "1.0.0")]);
        const string libraries = """{ "Parent/1.0.0": { "dependencies": { "Child": "1.0.0" } }, "Child/1.0.0": {} }""";
        WriteTargets(("net9.0", libraries), ("net10.0", libraries));

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project).Select(result => result.Name), Is.EqualTo(new[] { "Child" }));
    }

    [Test]
    public void FindRedundantPackagesDoesNotUseConditionalPackageAsRootInAnotherFramework()
    {
        project.Packages.AddRange([new PackageReference("Parent", "1.0.0"), new PackageReference("Child", "1.0.0")]);
        File.WriteAllText(project.AssetsPath, """
            {
              "targets": { "net10.0": { "Parent/1.0.0": { "dependencies": { "Child": "1.0.0" } }, "Child/1.0.0": {} } },
              "project": { "frameworks": { "net10.0": { "dependencies": { "Child": {} } } } }
            }
            """);

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project), Is.Empty);
    }

    [Test]
    public void AssetsWithoutTargetsProduceWarning()
    {
        File.WriteAllText(project.AssetsPath, "{}");

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project), Is.Empty);
        Assert.That(project.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void FindRedundantPackagesMatchesProjectByPathWhenAssemblyNameDiffers()
    {
        project.Packages.Add(new PackageReference("Child", "1.0.0"));
        project.ProjectReferences.Add(new ProjectReferenceInfo(Path.Combine(projectFolder, "Library.csproj")));
        File.WriteAllText(project.AssetsPath, """
            {
              "targets": { "net10.0": { "Custom.Assembly/1.0.0": { "type": "project", "dependencies": { "Child": "1.0.0" } }, "Child/1.0.0": {} } },
              "libraries": { "Custom.Assembly/1.0.0": { "type": "project", "msbuildProject": "Library.csproj" } }
            }
            """);

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project), Is.EqualTo(new[] { new DependencyAnalyzer.RedundancyInfo("Child", "Custom.Assembly") }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrMalformedAssetsProduceWarning(bool malformed)
    {
        if (malformed)
        {
            File.WriteAllText(project.AssetsPath, "{");
        }

        Assert.That(DependencyAnalyzer.FindRedundantPackages(project), Is.Empty);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);
        Assert.That(project.Warnings, Has.Count.EqualTo(1));
        Assert.That(project.Warnings[0], Does.Contain("dotnet restore"));
    }

    [Test]
    public void FindRedundantReferencesTerminatesForCyclicGraphs()
    {
        var library = new ProjectInfo("Library", Path.Combine(projectFolder, "Library.csproj"));
        var other = new ProjectInfo("Other", Path.Combine(projectFolder, "Other.csproj"));
        var unrelated = new ProjectInfo("Unrelated", Path.Combine(projectFolder, "Unrelated.csproj"));
        project.ProjectReferences.AddRange([new ProjectReferenceInfo(library.Path), new ProjectReferenceInfo(unrelated.Path)]);
        library.ProjectReferences.Add(new ProjectReferenceInfo(other.Path));
        other.ProjectReferences.Add(new ProjectReferenceInfo(library.Path));

        Assert.That(analyzer.FindRedundantReferences(project, [project, library, other, unrelated]), Is.Empty);
    }

    [Test]
    public void FindRedundantReferencesFindsDeepTransitiveReferences()
    {
        var library = new ProjectInfo("Library", Path.Combine(projectFolder, "Library.csproj"));
        var intermediate = new ProjectInfo("Intermediate", Path.Combine(projectFolder, "Intermediate.csproj"));
        var child = new ProjectInfo("Child", Path.Combine(projectFolder, "Child.csproj"));
        project.ProjectReferences.AddRange([new ProjectReferenceInfo(library.Path), new ProjectReferenceInfo(child.Path)]);
        library.ProjectReferences.Add(new ProjectReferenceInfo(intermediate.Path));
        intermediate.ProjectReferences.Add(new ProjectReferenceInfo(child.Path));

        Assert.That(analyzer.FindRedundantReferences(project, [project, library, intermediate, child]), Is.EqualTo(new[] { new DependencyAnalyzer.RedundancyInfo(child.Path, "Library") }));
    }

    [Test]
    public void AnalyzeSolutionParsesXmlAndLoadsProjectsOutsideSolution()
    {
        WriteProject("App.csproj", """<ItemGroup><ProjectReference Include="Library.csproj" /></ItemGroup>""");
        WriteProject("Library.csproj");
        var solutionPath = Path.Combine(projectFolder, "App.slnx");
        File.WriteAllText(solutionPath, """
            <Solution><Folder Name="/Source/"><Project
              Type='Classic C#' Path='App.csproj' /></Folder></Solution>
            """);
        var result = analyzer.AnalyzeSolution(solutionPath);

        Assert.That(result.Select(candidate => candidate.Name), Is.EqualTo(new[] { "App", "Library" }));
    }

    [Test]
    public void AnalyzeProjectHonorsVersionOverrideAndCustomAssetsPath()
    {
        WriteProject("App.csproj", """
            <PropertyGroup><ProjectAssetsFile>custom/project.assets.json</ProjectAssetsFile></PropertyGroup>
            <ItemGroup><PackageReference Include="Example" Version="1.0.0" VersionOverride="2.0.0" /></ItemGroup>
            """);
        var result = DependencyAnalyzer.AnalyzeProject(project.Path);

        Assert.That(result.Packages.Single().Version, Is.EqualTo("2.0.0"));
        Assert.That(result.AssetsPath, Is.EqualTo(Path.Combine(projectFolder, "custom", "project.assets.json")));
    }

    [Test]
    public void AnalyzeProjectIncludesConditionalReferencesFromAllFrameworks()
    {
        File.WriteAllText(project.Path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'"><PackageReference Include="Older" Version="1.0.0" /></ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><PackageReference Include="Newer" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        var result = DependencyAnalyzer.AnalyzeProject(project.Path);

        Assert.That(result.Packages.Select(package => package.Name), Is.EquivalentTo(new[] { "Older", "Newer" }));
    }

    [Test]
    public void AnalyzeProjectReportsPackageVersionsFromAllFrameworks()
    {
        File.WriteAllText(project.Path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'"><PackageReference Include="Example" Version="1.0.0" /></ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'"><PackageReference Include="Example" Version="2.0.0" /></ItemGroup>
            </Project>
            """);
        var result = DependencyAnalyzer.AnalyzeProject(project.Path);

        Assert.That(result.Packages.Single().Version, Is.EqualTo("1.0.0 / 2.0.0"));
    }

    private void WriteTargets(params (string Name, string Libraries)[] targets)
    {
        var assets = new { targets = targets.ToDictionary(target => target.Name, target => JsonSerializer.Deserialize<JsonElement>(target.Libraries)) };
        File.WriteAllText(project.AssetsPath, JsonSerializer.Serialize(assets));
    }

    private void WriteProject(string name, string items = "") => File.WriteAllText(Path.Combine(projectFolder, name), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{items}</Project>");
}
