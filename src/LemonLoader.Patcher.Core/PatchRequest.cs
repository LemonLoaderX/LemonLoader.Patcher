public sealed record SigningOptions(
    string KeystorePath,
    string StorePassword,
    string KeyAlias,
    string? KeyPassword = null);

internal enum PatchInputKind
{
    Apk,
    Directory
}

public sealed record PatchRequest
{
    public required string InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? ReleasePath { get; init; }
    public string? RuntimeVariant { get; init; }
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
    public bool AlignApk { get; init; }
    public string? ZipAlignPath { get; init; }
    public string? ApkSignerPath { get; init; }
    public SigningOptions? Signing { get; init; }
    internal PatchInputKind InputKind { get; init; }

    internal string ToolCacheRoot
    {
        get
        {
            var anchor = OutputPath ?? InputPath;
            return Path.Combine(Path.GetDirectoryName(anchor) ?? anchor, ".tools");
        }
    }

    internal ApkPostProcessingOptions PostProcessing => new(
        AlignApk,
        ZipAlignPath,
        Signing,
        ApkSignerPath);

    public PatchRequest NormalizeAndValidate()
    {
        if (RuntimeVariant is not null) RuntimeVariants.Normalize(RuntimeVariant);
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
            InputPath = FullPath(InputPath, "Input"),
            OutputPath = OptionalPath(OutputPath),
            ReleasePath = OptionalPath(ReleasePath),
            DeploymentPath = OptionalPath(DeploymentPath),
            GameAssemblyPath = OptionalPath(GameAssemblyPath),
            MetadataPath = OptionalPath(MetadataPath),
            UnityLibrariesPath = OptionalPath(UnityLibrariesPath),
            InteropOutputPath = OptionalPath(InteropOutputPath),
            Cpp2IlPath = OptionalPath(Cpp2IlPath),
            Il2CppInteropCliPath = OptionalPath(Il2CppInteropCliPath),
            ZipAlignPath = OptionalPath(ZipAlignPath),
            ApkSignerPath = OptionalPath(ApkSignerPath),
            Signing = Signing is null
                ? null
                : Signing with { KeystorePath = FullPath(Signing.KeystorePath, "Keystore") }
        };
        var inputIsFile = File.Exists(normalized.InputPath);
        var inputIsDirectory = Directory.Exists(normalized.InputPath);
        if (!inputIsFile && !inputIsDirectory)
            throw new ArgumentException($"Input APK or directory was not found at '{normalized.InputPath}'.");
        normalized = normalized with
        {
            InputKind = inputIsDirectory ? PatchInputKind.Directory : PatchInputKind.Apk
        };
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (inputIsDirectory)
        {
            if (normalized.OutputPath is not null)
                throw new ArgumentException("Directory input is patched in place and does not accept --output.");
            if (normalized.AlignApk || normalized.ZipAlignPath is not null ||
                normalized.Signing is not null || normalized.ApkSignerPath is not null)
            {
                throw new ArgumentException(
                    "Directory input does not support APK alignment or signing options.");
            }
        }
        else
        {
            if (normalized.OutputPath is null)
                throw new ArgumentException("APK input requires an output APK path.");
            if (string.Equals(normalized.InputPath, normalized.OutputPath, comparison))
                throw new ArgumentException("The output APK must not overwrite the input APK.");
            if (Directory.Exists(normalized.OutputPath))
                throw new ArgumentException($"Output APK path '{normalized.OutputPath}' is a directory.");
            if (normalized.ZipAlignPath is not null && !normalized.AlignApk)
                throw new ArgumentException("--zipalign requires --align.");
            if (normalized.ApkSignerPath is not null && normalized.Signing is null)
                throw new ArgumentException("--apksigner requires --keystore.");
        }
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
        if (normalized.Signing is { } normalizedSigning &&
            !File.Exists(normalizedSigning.KeystorePath))
        {
            throw new ArgumentException(
                $"Signing keystore was not found at '{normalizedSigning.KeystorePath}'.");
        }
        return normalized;
    }
}

public sealed record PatchResult(
    string OutputPath,
    string? Sha256,
    string UnityVersion,
    bool ModifiedInPlace);

public enum PatcherMessageKind
{
    Stage,
    ToolOutput,
    Warning
}

public sealed record PatcherMessage(PatcherMessageKind Kind, string Text);
