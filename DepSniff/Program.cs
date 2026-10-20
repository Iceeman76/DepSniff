using DepSniff.Commands;
using Microsoft.Build.Locator;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DepSniff;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (MSBuildLocator.IsRegistered is false)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to initialize MSBuild locator.");
            AnsiConsole.WriteException(exception);

            return 1;
        }

        var app = new CommandApp<AnalyzeCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("depsniff");
            config.AddCommand<AnalyzeCommand>("analyze")
                .WithDescription("Analyzes project dependencies (NuGet and Project)")
                .WithExample(["analyze", "DepSniff.slnx"])
                .WithExample(["analyze", "DepSniff/DepSniff.csproj"]);
        });

        try
        {
            return app.Run(args);
        }
        catch (Exception exception)
        {
            AnsiConsole.WriteException(exception);

            return 1;
        }
    }
}
