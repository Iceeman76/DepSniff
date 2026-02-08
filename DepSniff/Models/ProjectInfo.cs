namespace DepSniff.Models;

public record ProjectReferenceInfo(string Path)
{
    public bool IsUsed { get; set; } = true;
}

public record ProjectInfo(string Name, string Path)
{
    public List<PackageReference> Packages { get; init; } = [];
    public List<ProjectReferenceInfo> ProjectReferences { get; init; } = [];
}

public record PackageReference(string Name, string Version, bool IsTransitive = false, string? Source = null)
{
    public bool IsUsed { get; set; } = true;
}
