public sealed record UnityDependenciesRequest
{
    public required string UnityVersion { get; init; }
    public required string OutputPath { get; init; }
    public string? CachePath { get; init; }
}

public sealed record UnityDependenciesResult(
    string OutputPath,
    string PackageVersion,
    string Source,
    int AssemblyCount);

public static class UnityDependenciesPipeline
{
    public static async Task<UnityDependenciesResult> RunAsync(
        UnityDependenciesRequest request,
        IProgress<PatcherMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UnityVersion))
            throw new ArgumentException("Unity version is required.");
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new ArgumentException("Output directory is required.");

        var outputPath = Path.GetFullPath(request.OutputPath);
        var outputParent = Path.GetDirectoryName(outputPath) ?? outputPath;
        var cachePath = string.IsNullOrWhiteSpace(request.CachePath)
            ? Path.Combine(outputParent, ".tools", "UnityDependencies")
            : Path.GetFullPath(request.CachePath);
        if (ContainsPath(outputPath, cachePath) || ContainsPath(cachePath, outputPath))
        {
            throw new ArgumentException(
                "Unity dependency output and cache directories must not contain each other.");
        }

        progress?.Report(new(PatcherMessageKind.Stage, "Resolving Unity dependencies"));
        var resolution = await UnityDependenciesResolver.ResolveAsync(
            cachePath,
            request.UnityVersion,
            progress,
            cancellationToken);
        UnityDependenciesResolver.Publish(resolution, outputPath);
        progress?.Report(new(
            PatcherMessageKind.Stage,
            $"Published {resolution.AssemblyCount} Unity assemblies"));
        return new(
            outputPath,
            resolution.PackageVersion,
            resolution.Source,
            resolution.AssemblyCount);
    }

    private static bool ContainsPath(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." ||
               (!relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison) &&
                !Path.IsPathRooted(relative));
    }
}
