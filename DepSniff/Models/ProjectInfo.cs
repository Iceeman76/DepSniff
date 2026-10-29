namespace DotNetLab.DepSniff.Models;

public sealed record ProjectReferenceInfo(string Path)
{
    public DependencyUsage Usage { get; set; }
}

public sealed record ProjectInfo(string Name, string Path)
{
    public List<PackageReference> Packages { get; init; } = [];
    public List<ProjectReferenceInfo> ProjectReferences { get; init; } = [];
    public List<string> TargetFrameworks { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public string AssetsPath { get; init; } = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "obj", "project.assets.json");
}

public sealed record PackageReference(string Name, string Version)
{
    public DependencyUsage Usage { get; set; }
}
