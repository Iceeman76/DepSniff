using System.ComponentModel;
using DotNetLab.DepSniff.Models;
using DotNetLab.DepSniff.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DotNetLab.DepSniff.Commands;

public sealed class AnalyzeCommand : AsyncCommand<AnalyzeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT_OR_SOLUTION]")]
        [Description("Path to .slnx or .csproj file. If omitted, searches for a single file in the current directory.")]
        public string? Path { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(settings.Path) ? Directory.GetCurrentDirectory() : settings.Path;

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path).Where(IsSupportedFile).Order(StringComparer.OrdinalIgnoreCase).ToList();

            if (files.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No .slnx or .csproj file found in [yellow]{Markup.Escape(path)}[/].");

                return 1;
            }

            if (files.Count > 1)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Multiple .slnx or .csproj files found in [yellow]{Markup.Escape(path)}[/]. Please specify which one to analyze.");

                foreach (var file in files)
                {
                    AnsiConsole.WriteLine($"  - {Path.GetFileName(file)}");
                }

                return 1;
            }

            path = files[0];
            AnsiConsole.MarkupLine($"Using file: [green]{Markup.Escape(path)}[/]");
        }

        if (File.Exists(path) is false)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File [yellow]{Markup.Escape(path)}[/] not found.");

            return 1;
        }

        if (IsSupportedFile(path) is false)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Only .slnx or .csproj files are supported.");

            return 1;
        }

        var analyzer = new DependencyAnalyzer();
        var usageAnalyzer = new SemanticUsageAnalyzer();
        var projects = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Analyzing...", async status =>
        {
            void OnProgress(string message) => status.Status(Markup.Escape(message));

            var loadedProjects = path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? analyzer.AnalyzeSolution(path, OnProgress) : analyzer.AnalyzeProjects([path], OnProgress);

            foreach (var project in loadedProjects)
            {
                await usageAnalyzer.AnalyzeAsync(project, OnProgress, cancellationToken);
            }

            return loadedProjects;
        });

        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No projects found to analyze.[/]");

            return 0;
        }

        AnsiConsole.WriteLine("Usage is determined by the C# compiler. Transitive references are removal candidates; verify versions, build assets and all target frameworks before removing them.");

        foreach (var project in projects)
        {
            var redundantProjects = analyzer.FindRedundantReferences(project, projects);
            var redundantPackages = DependencyAnalyzer.FindRedundantPackages(project);
            var table = new Table().AddColumn("Type").AddColumn("Name").AddColumn("Version").AddColumn("Usage").AddColumn("Transitive via");

            foreach (var package in project.Packages)
            {
                var redundancy = redundantPackages.FirstOrDefault(candidate => candidate.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase));
                table.AddRow("NuGet", Markup.Escape(package.Name), Markup.Escape(package.Version), FormatUsage(package.Usage), Markup.Escape(redundancy?.Source ?? "-"));
            }

            foreach (var reference in project.ProjectReferences)
            {
                var redundancy = redundantProjects.FirstOrDefault(candidate => candidate.Name.Equals(reference.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                table.AddRow("Project", Markup.Escape(Path.GetFileNameWithoutExtension(reference.Path)), "-", FormatUsage(reference.Usage), Markup.Escape(redundancy?.Source ?? "-"));
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  Project: [green]{Markup.Escape(project.Name)}[/]");

            foreach (var warning in project.Warnings)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");
            }

            AnsiConsole.Write(table);
        }

        return 0;
    }

    private static bool IsSupportedFile(string path) => path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static string FormatUsage(DependencyUsage usage) => usage switch
    {
        DependencyUsage.Used => "[green]Used (compiler verified)[/]",
        DependencyUsage.NotUsed => "[yellow]No compiler usage found[/]",
        DependencyUsage.NoCompileAssets => "[yellow]No compile assets (review build/runtime)[/]",
        _ => "[yellow]Unknown (analysis incomplete)[/]"
    };
}
