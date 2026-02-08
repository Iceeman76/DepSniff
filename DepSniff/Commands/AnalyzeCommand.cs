using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using DepSniff.Services;
using DepSniff.Models;

namespace DepSniff.Commands;

public class AnalyzeCommand : AsyncCommand<AnalyzeCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT_OR_SOLUTION]")]
        [Description("Path to .slnx or .csproj file. If omitted, searches for a single file in the current directory.")]
        public string? Path { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        var path = settings.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Directory.GetCurrentDirectory();
        }

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.*")
                .Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No .slnx or .csproj file found in [yellow]{path}[/].");
                return 1;
            }

            if (files.Count > 1)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Multiple .slnx or .csproj files found in [yellow]{path}[/]. Please specify which one to analyze.");
                foreach (var file in files)
                {
                    AnsiConsole.MarkupLine($"  - {Path.GetFileName(file)}");
                }
                return 1;
            }

            path = files[0];
            AnsiConsole.MarkupLine($"Using file: [green]{path}[/]");
        }

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File [yellow]{path}[/] not found.");
            return 1;
        }

        var analyzer = new DependencyAnalyzer();
        var projects = new List<ProjectInfo>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Analyzing...", async ctx =>
            {
                Action<string> onProgress = (msg) =>
                {
                    ctx.Status(msg);
                    AnsiConsole.MarkupLine($"[grey]>[/] {msg}");
                };

                if (path.EndsWith(".slnx"))
                {
                    projects = analyzer.AnalyzeSolution(path, onProgress);
                }
                else if (path.EndsWith(".csproj"))
                {
                    onProgress("Loading project...");
                    projects = [DependencyAnalyzer.AnalyzeProject(path)];
                }
                else
                {
                    throw new Exception("Only .slnx or .csproj are supported.");
                }

                foreach (var project in projects)
                {
                    onProgress($"Analyzing redundancies for {project.Name}...");
                    // We calculate them once here to avoid doing it twice in the loop
                    // But for now, we just want the progress to be shown
                }

                // Move usage analysis here to show progress BEFORE tables
                foreach (var project in projects)
                {
                    // Use ctx.Status for usage analysis to NOT print it to console
                    ctx.Status($"Analyzing usage for {project.Name}...");
                    analyzer.AnalyzeUsage(project);
                }

                onProgress("Finalizing analysis...");
            });

        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No projects found to analyze.[/]");
            return 0;
        }

        foreach (var project in projects)
        {
            var redundantProjects = analyzer.FindRedundantReferences(project, projects);
            var redundantPackages = DependencyAnalyzer.FindRedundantPackages(project);

            var table = new Table();
            table.AddColumn("Type");
            table.AddColumn("Name");
            table.AddColumn("Version");
            table.AddColumn("Status");

            foreach (var pkg in project.Packages.Where(p => !p.IsTransitive))
            {
                var redundancy =
                    redundantPackages.FirstOrDefault(r => r.Name.Equals(pkg.Name, StringComparison.OrdinalIgnoreCase));
                
                string status;
                if (redundancy != null)
                {
                    status = $"[red]Redundant (Transitive via {redundancy.Source})[/]";
                }
                else if (!pkg.IsUsed)
                {
                    status = "[yellow]Uncertain (Not found in code)[/]";
                }
                else
                {
                    status = "[green]Necessary[/]";
                }
                
                table.AddRow("NuGet", pkg.Name, pkg.Version, status);
            }

            foreach (var pr in project.ProjectReferences)
            {
                var redundancy =
                    redundantProjects.FirstOrDefault(r => r.Name.Equals(pr.Path, StringComparison.OrdinalIgnoreCase));
                
                string status;
                if (redundancy != null)
                {
                    status = $"[red]Redundant (Transitive via {redundancy.Source})[/]";
                }
                else if (!pr.IsUsed)
                {
                    status = "[yellow]Uncertain (Not found in code)[/]";
                }
                else
                {
                    status = "[green]Necessary[/]";
                }
                
                table.AddRow("Project", Path.GetFileNameWithoutExtension(pr.Path), "-", status);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  Project: [green]{project.Name}[/]");
            AnsiConsole.Write(table);
        }

        return 0;
    }
}
