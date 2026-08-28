public sealed record SigningOptions(
    string KeystorePath,
    string StorePassword,
    string KeyAlias,
    string? KeyPassword = null);

public sealed record PatchRequest
{
    public required string InputApkPath { get; init; }
    public required string OutputApkPath { get; init; }
    public string? ReleasePath { get; init; }
    public string? DeploymentPath { get; init; }
    public DeploymentPolicyOptions DeploymentPolicies { get; init; } =
        DeploymentPolicyOptions.Create(null, []);
    public string? GameAssemblyPath { get; init; }
    public string? MetadataPath { get; init; }
    public string? UnityVersion { get; init; }
    public string? UnityLibrariesPath { get; init; }
    public string? InteropOutputPath { get; init; }
    public string? Cpp2IlPath { get; init; }
    public string? Il2CppInteropCliPath { get; init; }
    public string? AndroidSdkRoot { get; init; }
    public SigningOptions? Signing { get; init; }

    public PatchRequest NormalizeAndValidate()
    {
        static string FullPath(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{name} is required.");
            return Path.GetFullPath(value);
        }

        static string? OptionalPath(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);

        var normalized = this with
        {
            InputApkPath = FullPath(InputApkPath, "Input APK"),
            OutputApkPath = FullPath(OutputApkPath, "Output APK"),
            ReleasePath = OptionalPath(ReleasePath),
            DeploymentPath = OptionalPath(DeploymentPath),
            GameAssemblyPath = OptionalPath(GameAssemblyPath),
            MetadataPath = OptionalPath(MetadataPath),
            UnityLibrariesPath = OptionalPath(UnityLibrariesPath),
            InteropOutputPath = OptionalPath(InteropOutputPath),
            Cpp2IlPath = OptionalPath(Cpp2IlPath),
            Il2CppInteropCliPath = OptionalPath(Il2CppInteropCliPath),
            AndroidSdkRoot = OptionalPath(AndroidSdkRoot),
            Signing = Signing is null
                ? null
                : Signing with { KeystorePath = FullPath(Signing.KeystorePath, "Keystore") }
        };
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(normalized.InputApkPath, normalized.OutputApkPath, comparison))
            throw new ArgumentException("The output APK must not overwrite the input APK.");
        if (normalized.DeploymentPath is not null &&
            !Directory.Exists(normalized.DeploymentPath))
        {
            throw new DirectoryNotFoundException(
                $"Deployment directory was not found at '{normalized.DeploymentPath}'.");
        }
        if (normalized.Il2CppInteropCliPath is { } interopCliPath)
        {
            if (!File.Exists(interopCliPath))
                throw new ArgumentException($"Il2CppInterop CLI was not found at '{interopCliPath}'.");
            if (!string.Equals(Path.GetExtension(interopCliPath), ".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The Il2CppInterop CLI override must be a managed .dll file.");
        }
        if (normalized.Signing is { } signing &&
            (string.IsNullOrWhiteSpace(signing.StorePassword) ||
             string.IsNullOrWhiteSpace(signing.KeyAlias)))
        {
            throw new ArgumentException("Signing requires a keystore password and key alias.");
        }
        return normalized;
    }
}

public sealed record PatchResult(
    string OutputApkPath,
    string Sha256,
    string UnityVersion);

public enum PatcherMessageKind
{
    Stage,
    ToolOutput,
    Warning
}

public sealed record PatcherMessage(PatcherMessageKind Kind, string Text);

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

        static bool ContainsPath(string parent, string child)
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
}
