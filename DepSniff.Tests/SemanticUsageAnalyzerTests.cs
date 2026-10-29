using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DotNetLab.DepSniff.Models;
using DotNetLab.DepSniff.Services;
using NUnit.Framework;

namespace DotNetLab.DepSniff.Tests;

[TestFixture]
public sealed class SemanticUsageAnalyzerTests
{
    private string fixtureFolder = null!;
    private string projectFolder = null!;
    private string feedFolder = null!;
    private string projectPath = null!;

    [OneTimeSetUp]
    public async Task CreateLocalPackagesAsync()
    {
        fixtureFolder = Path.Combine(Path.GetTempPath(), "DepSniff.SemanticTests", Guid.NewGuid().ToString("N"));
        feedFolder = Directory.CreateDirectory(Path.Combine(fixtureFolder, "feed")).FullName;
        var source = """
            namespace Unrelated.Namespace;
            public sealed class WidgetAttribute : System.Attribute {}
            public interface IWidget {}
            public sealed class Widget : IWidget {}
            public static class WidgetExtensions
            {
                public static int Measure(this string value) => value.Length;
            }
            """;
        var assemblyFolder = Directory.CreateDirectory(Path.Combine(fixtureFolder, "assembly")).FullName;
        var assemblyProject = Path.Combine(assemblyFolder, "Different.Assembly.csproj");
        await File.WriteAllTextAsync(assemblyProject, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        await File.WriteAllTextAsync(Path.Combine(assemblyFolder, "Library.cs"), source);
        await RunDotNetAsync("build", assemblyProject, "--configuration", "Release", "--source", feedFolder, "-p:NuGetAudit=false");
        await CreatePackageAsync("Fixture.Package", "lib/net10.0/Different.Assembly.dll", await File.ReadAllBytesAsync(Path.Combine(assemblyFolder, "bin", "Release", "net10.0", "Different.Assembly.dll")));
        await CreatePackageAsync("Build.Only", "build/Build.Only.targets", Encoding.UTF8.GetBytes("<Project />"));
    }

    [OneTimeTearDown]
    public void DeleteLocalPackages() => Directory.Delete(fixtureFolder, true);

    [SetUp]
    public void SetUp()
    {
        projectFolder = Directory.CreateDirectory(Path.Combine(fixtureFolder, Guid.NewGuid().ToString("N"))).FullName;
        projectPath = Path.Combine(projectFolder, "App.csproj");
    }

    [TestCase("public sealed class App { public Unrelated.Namespace.Widget Create() => new(); }", DependencyUsage.Used)]
    [TestCase("[Unrelated.Namespace.Widget] public sealed class App {}", DependencyUsage.Used)]
    [TestCase("using Unrelated.Namespace; public sealed class App { public int Count() => string.Empty.Measure(); }", DependencyUsage.Used)]
    [TestCase("using Alias = Unrelated.Namespace.Widget; public sealed class App { public Alias Create() => new(); }", DependencyUsage.Used)]
    [TestCase("using Unrelated.Namespace; public sealed class App { public object Create() { var widget = new Widget(); return widget; } }", DependencyUsage.Used)]
    [TestCase("public sealed class App { public System.Collections.Generic.List<Unrelated.Namespace.Widget> Values { get; } }", DependencyUsage.Used)]
    [TestCase("public sealed class App { public string Name => \"Fixture.Package Different.Assembly Unrelated.Namespace\"; }", DependencyUsage.NotUsed)]
    [TestCase("// Fixture.Package Different.Assembly Unrelated.Namespace\npublic sealed class App {}", DependencyUsage.NotUsed)]
    [TestCase("using Unrelated.Namespace; public sealed class App {}", DependencyUsage.NotUsed)]
    [TestCase("#if NEVER_DEFINED\npublic sealed class App { public Unrelated.Namespace.Widget Value { get; } }\n#endif\npublic sealed class Empty {}", DependencyUsage.NotUsed)]
    public async Task UsesCompilerReferencesInsteadOfTextMatches(string source, DependencyUsage expected)
    {
        await WriteProjectAsync();
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "App.cs"), source);
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);

        Assert.That(project.Warnings, Is.Empty, string.Join(Environment.NewLine, project.Warnings));
        Assert.That(project.Packages.Single().Usage, Is.EqualTo(expected));
    }

    [Test]
    public async Task CompilationErrorsProduceUnknownRatherThanUnused()
    {
        await WriteProjectAsync();
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "App.cs"), "public sealed class App { public MissingType Value { get; } }");
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);

        Assert.That(project.Packages.Single().Usage, Is.EqualTo(DependencyUsage.Unknown));
        Assert.That(project.Warnings, Has.Some.Contains("CS0246"));
    }

    [Test]
    public async Task BuildOnlyPackagesAreReportedSeparately()
    {
        await WriteProjectAsync("Build.Only");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "App.cs"), "public sealed class App {}");
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);

        Assert.That(project.Warnings, Is.Empty, string.Join(Environment.NewLine, project.Warnings));
        Assert.That(project.Packages.Single().Usage, Is.EqualTo(DependencyUsage.NoCompileAssets));
    }

    [Test]
    public async Task HonorsLinkedAndExcludedCompileItems()
    {
        await WriteProjectAsync(items: """
            <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
            <ItemGroup><Compile Include="Linked.cs" Link="Source/Linked.cs" /></ItemGroup>
            """);
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "Linked.cs"), "public sealed class App {}");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "Excluded.cs"), "public sealed class Excluded { public Unrelated.Namespace.Widget Value { get; } }");
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        var analyzer = new SemanticUsageAnalyzer();
        await analyzer.AnalyzeAsync(project);

        Assert.That(project.Packages.Single().Usage, Is.EqualTo(DependencyUsage.NotUsed));
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "Linked.cs"), "public sealed class App { public Unrelated.Namespace.Widget Value { get; } }");
        await analyzer.AnalyzeAsync(project);

        Assert.That(project.Warnings, Is.Empty, string.Join(Environment.NewLine, project.Warnings));
        Assert.That(project.Packages.Single().Usage, Is.EqualTo(DependencyUsage.Used));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ProjectReferencesAreMatchedByAssemblyIdentity(bool used)
    {
        var libraryFolder = Directory.CreateDirectory(Path.Combine(fixtureFolder, Guid.NewGuid().ToString("N"))).FullName;
        var libraryPath = Path.Combine(libraryFolder, "Library.csproj");
        await File.WriteAllTextAsync(libraryPath, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Custom.Identity</AssemblyName></PropertyGroup></Project>""");
        await File.WriteAllTextAsync(Path.Combine(libraryFolder, "Library.cs"), "namespace Another.Namespace; public sealed class Widget {}");
        await File.WriteAllTextAsync(projectPath, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"{libraryPath}\" /></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "App.cs"), used ? "public sealed class App { public Another.Namespace.Widget Value { get; } }" : "public sealed class App { public string Name => \"Library Custom.Identity Another.Namespace\"; }");
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);

        Assert.That(project.Warnings, Is.Empty, string.Join(Environment.NewLine, project.Warnings));
        Assert.That(project.ProjectReferences.Single().Usage, Is.EqualTo(used ? DependencyUsage.Used : DependencyUsage.NotUsed));
    }

    [Test]
    public async Task CombinesUsageAcrossFrameworksWithConditionalCompilation()
    {
        await WriteProjectAsync(items: "<PropertyGroup><TargetFramework /><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup>");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "App.cs"), """
            public sealed class App
            {
            #if WINDOWS
                public Unrelated.Namespace.Widget Value { get; }
            #endif
            }
            """);
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);

        Assert.That(project.Warnings, Is.Empty, string.Join(Environment.NewLine, project.Warnings));
        Assert.That(project.Packages.Single().Usage, Is.EqualTo(DependencyUsage.Used));
    }

    [Test]
    public async Task FailedFrameworkPreventsUnusedConclusion()
    {
        await WriteProjectAsync(items: "<PropertyGroup><TargetFramework /><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup>");
        await File.WriteAllTextAsync(Path.Combine(projectFolder, "App.cs"), """
            public sealed class App
            {
            #if WINDOWS
                public MissingType Value { get; }
            #endif
            }
            """);
        await RestoreAsync();
        var project = DependencyAnalyzer.AnalyzeProject(projectPath);
        await new SemanticUsageAnalyzer().AnalyzeAsync(project);

        Assert.That(project.Warnings, Has.Some.Contains("CS0246"));
        Assert.That(project.Packages.Single().Usage, Is.EqualTo(DependencyUsage.Unknown));
    }

    private async Task CreatePackageAsync(string name, string assetPath, byte[] content)
    {
        await using var package = await ZipFile.OpenAsync(Path.Combine(feedFolder, $"{name}.1.0.0.nupkg"), ZipArchiveMode.Create);
        await using (var writer = new StreamWriter(await package.CreateEntry($"{name}.nuspec").OpenAsync()))
        {
            await writer.WriteAsync($"<package><metadata><id>{name}</id><version>1.0.0</version><authors>DepSniff.Tests</authors><description>Semantic analysis test fixture</description></metadata></package>");
        }

        await using var asset = await package.CreateEntry(assetPath).OpenAsync();
        await asset.WriteAsync(content);
    }

    private Task WriteProjectAsync(string packageName = "Fixture.Package", string items = "") => File.WriteAllTextAsync(projectPath, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"{packageName}\" Version=\"1.0.0\" /></ItemGroup>{items}</Project>");

    private Task RestoreAsync() => RunDotNetAsync("restore", projectPath, "--source", feedFolder, "--packages", Path.Combine(projectFolder, "packages"), "-p:NuGetAudit=false");

    private static async Task RunDotNetAsync(params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.That(process.ExitCode, Is.Zero, await output + await errors);
    }
}
