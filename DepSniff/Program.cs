using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Build.Locator;
using Spectre.Console;
using Spectre.Console.Cli;
using DepSniff.Commands;

namespace DepSniff;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                // Try RegisterDefaults first, but it often fails in single-file apps
                try 
                {
                    MSBuildLocator.RegisterDefaults();
                }
                catch (DllNotFoundException)
                {
                    // If hostfxr is not found, try to find MSBuild manually
                    var dotNetPath = GetDotNetPath();
                    if (!string.IsNullOrEmpty(dotNetPath))
                    {
                        MSBuildLocator.RegisterMSBuildPath(dotNetPath);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to initialize MSBuild locator.");
            AnsiConsole.WriteException(ex);
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
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private static string? GetDotNetPath()
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-sdks",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                // Get the latest SDK
                var lastLine = lines.LastOrDefault();
                if (lastLine != null)
                {
                    var parts = lastLine.Split('[');
                    if (parts.Length > 1)
                    {
                        var version = parts[0].Trim();
                        var path = parts[1].Trim(' ', ']');
                        return Path.Combine(path, version);
                    }
                }
            }
        }
        catch
        {
            // Ignore and return null
        }
        return null;
    }
}
