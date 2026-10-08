using System.IO.Compression;

namespace LemonLoader.Patcher.Core;

public sealed class ApkPatchPipeline
{
    private readonly PatchRequest request;
    private readonly IProgress<PatcherMessage>? progress;

    public ApkPatchPipeline(
        PatchRequest request,
        IProgress<PatcherMessage>? progress = null)
    {
        this.request = request.NormalizeAndValidate();
        this.progress = progress;
    }

    public async Task<PatchResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ReportStage("Validating inputs");
        ResolvedApkPostProcessing? postProcessing = null;
        if (request.InputKind == PatchInputKind.Apk)
        {
            postProcessing = ApkPostProcessor.Resolve(request.PostProcessing);
            RequestPaths.ValidateOutputFile(request.OutputPath!,
                postProcessing.ZipAlignPath, postProcessing.ApkSignerPath);
            if (request.Generation?.OutputPath is { } export)
                RequestPaths.ValidateExport(export,
                    postProcessing.ZipAlignPath, postProcessing.ApkSignerPath);
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath!)!);
        }
        ValidateGameLayout();

        using var workspace = new PatchWorkspace(progress);
        var workRoot = workspace.Root;
        var release = await ResolveReleaseAsync(workRoot, cancellationToken);
        string interopRoot;
        string? unityVersion = null;
        if (request.InteropInputPath is { } existing)
        {
            ReportStage("Using supplied Interop DLLs");
            interopRoot = Path.Combine(workRoot, "interop");
            InteropInput.Copy(existing, interopRoot, cancellationToken);
        }
        else
        {
            var generated = await new GameInteropGenerator(request.Generation!, progress)
                .GenerateAsync(workRoot, cancellationToken);
            interopRoot = generated.DirectoryPath;
            unityVersion = generated.UnityVersion;
        }
        var payload = new PayloadSource(release.ReleaseRoot, interopRoot, request.DeploymentPath, request.DeploymentPolicies);

        if (request.InputKind == PatchInputKind.Directory)
        {
            ReportStage("Injecting payload into directory");
            PayloadAssembler.InjectDirectory(request.InputPath, payload, progress, cancellationToken);
            ReportStage("Directory ready");
            return new(request.InputPath, null, unityVersion, true);
        }

        ReportStage("Packaging APK payload");
        var patchedApk = Path.Combine(workRoot, "patched.apk");
        DirectoryPublisher.CopyFile(request.InputPath, patchedApk, cancellationToken);
        PayloadAssembler.MergeApk(patchedApk, payload, cancellationToken);
        var hash = await ApkPostProcessor.PublishAsync(patchedApk, request.OutputPath!, workRoot,
            postProcessing!, progress, cancellationToken);
        ReportStage("APK ready");
        return new(request.OutputPath!, hash, unityVersion, false);
    }

    private async Task<ReleaseValidationResult> ResolveReleaseAsync(
        string workRoot,
        CancellationToken cancellationToken)
    {
        ReportStage("Resolving LemonLoader Release");
        var releaseRoot = Path.Combine(workRoot, "release");
        var releaseArchive = request.ReleasePath ?? await ReleaseResolver.ResolveLatestAsync(
            request.ToolCacheRoot,
            progress,
            cancellationToken,
            RuntimeVariants.Normalize(request.RuntimeVariant), releaseRoot);
        if (!File.Exists(releaseArchive))
        {
            throw new FileNotFoundException(
                $"LemonLoader Release was not found at '{releaseArchive}'.");
        }
        if (request.ReleasePath is null) return new ReleaseValidationResult(releaseRoot);
        using (var archive = ZipFile.OpenRead(releaseArchive))
        {
            ArchiveSafety.Extract(archive, releaseRoot, cancellationToken);
        }
        var validation = ReleaseValidator.Validate(releaseRoot, cancellationToken);
        if (request.RuntimeVariant is not null)
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(releaseRoot, "lemonloader-release.json")));
            RuntimeVariants.ValidateManifest(manifest.RootElement, RuntimeVariants.Normalize(request.RuntimeVariant));
        }
        return validation;
    }

    private void ValidateGameLayout()
    {
        bool hasMain, hasUnity;
        if (request.InputKind == PatchInputKind.Directory)
        {
            hasMain = File.Exists(GamePackageLayout.FilePath(request.InputPath, GamePackageLayout.MainLibrary));
            hasUnity = File.Exists(GamePackageLayout.FilePath(request.InputPath, GamePackageLayout.UnityLibrary));
        }
        else
        {
            using var apk = ZipFile.OpenRead(request.InputPath);
            ArchiveSafety.Validate(apk);
            hasMain = apk.GetEntry(GamePackageLayout.MainLibrary) is not null;
            hasUnity = apk.GetEntry(GamePackageLayout.UnityLibrary) is not null;
        }
        if (!hasMain || !hasUnity)
            throw new InvalidOperationException("Injection requires the standard ARM64 Unity libmain.so startup layout.");
    }

    private void ReportStage(string message) =>
        progress?.Report(new(PatcherMessageKind.Stage, message));
}
