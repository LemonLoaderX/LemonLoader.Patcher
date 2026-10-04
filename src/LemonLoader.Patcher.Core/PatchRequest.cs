namespace LemonLoader.Patcher.Core;

internal enum PatchInputKind { Apk, Directory }

public sealed record PatchRequest
{
    public required string InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? ReleasePath { get; init; }
    public string? RuntimeVariant { get; init; }
    public string? DeploymentPath { get; init; }
    public DeploymentPolicyOptions DeploymentPolicies { get; init; } = DeploymentPolicyOptions.Create(null, []);
    public string? InteropInputPath { get; init; }
    public InteropRequest? Generation { get; init; }
    public ApkPostProcessingOptions PostProcessing { get; init; } = new();
    internal PatchInputKind InputKind { get; init; }
    internal string ToolCacheRoot => ToolCachePaths.Root;

    public PatchRequest NormalizeAndValidate()
    {
        if (RuntimeVariant is not null)
            RuntimeVariants.Normalize(RuntimeVariant);
        var request = this with
        {
            InputPath = RequestPaths.Optional(InputPath) ?? throw new ArgumentException("Input is required."),
            OutputPath = RequestPaths.Optional(OutputPath), ReleasePath = RequestPaths.Optional(ReleasePath),
            DeploymentPath = RequestPaths.Optional(DeploymentPath), InteropInputPath = RequestPaths.Optional(InteropInputPath),
            PostProcessing = PostProcessing.NormalizeAndValidate()
        };
        bool directory = Directory.Exists(request.InputPath);
        if (!directory && !File.Exists(request.InputPath))
            throw new ArgumentException($"Input APK or directory was not found at '{request.InputPath}'.");
        request = request with { InputKind = directory ? PatchInputKind.Directory : PatchInputKind.Apk };
        if (directory)
        {
            PathSafety.RejectLinks(request.InputPath);
            if (request.OutputPath is not null)
                throw new ArgumentException("Directory input is patched in place and does not accept --output.");
            if (request.PostProcessing != new ApkPostProcessingOptions())
                throw new ArgumentException("Directory input does not support APK alignment or signing options.");
        }
        else if (request.OutputPath is null)
            throw new ArgumentException("APK input requires an output APK path.");
        if (request.DeploymentPath is { } deployment && !Directory.Exists(deployment))
            throw new DirectoryNotFoundException($"Deployment directory was not found at '{deployment}'.");
        if (request.InteropInputPath is { } interop)
        {
            _ = InteropInput.Assemblies(interop);
            if (request.Generation is not null)
                throw new ArgumentException("Existing --interop DLLs cannot be combined with generation/export options.");
        }
        else
        {
            var generation = request.Generation ?? new InteropRequest();
            if (generation.InputPath is { } input && Path.GetRelativePath(request.InputPath, Path.GetFullPath(input)) != ".")
                throw new ArgumentException("Composed generation must use the patch input.");
            request = request with
            {
                Generation = (generation with { InputPath = request.InputPath }).NormalizeAndValidate()
            };
        }
        var processing = request.PostProcessing;
        var inputs = new[] { request.InputPath, request.ReleasePath, request.DeploymentPath, request.InteropInputPath,
            request.Generation?.GameAssemblyPath, request.Generation?.MetadataPath, request.Generation?.UnityLibrariesPath,
            request.Generation?.Cpp2IlPath, request.Generation?.Il2CppInteropCliPath,
            request.Generation?.Il2CppInteropCliPath is { } generator ? Path.GetDirectoryName(generator) : null, processing.ZipAlignPath,
            processing.ApkSignerPath, processing.Signing?.KeystorePath };
        if (request.Generation?.OutputPath is { } export)
            RequestPaths.ValidateExport(export, inputs.Append(request.OutputPath).Append(ToolCachePaths.Root).ToArray());
        if (request.OutputPath is { } output)
            RequestPaths.ValidateOutputFile(output, inputs);
        return request;
    }
}

public sealed record PatchResult(string OutputPath, string? Sha256, string? UnityVersion, bool ModifiedInPlace);
public enum PatcherMessageKind { Stage, ToolOutput, Warning }
public sealed record PatcherMessage(PatcherMessageKind Kind, string Text);
