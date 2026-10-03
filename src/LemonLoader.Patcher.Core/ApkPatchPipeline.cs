using System.IO.Compression;

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
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath!)!);
        }

        var workRoot = Path.Combine(
            Path.GetTempPath(),
            $"lemonloader-patcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var release = await ResolveReleaseAsync(workRoot, cancellationToken);
            var generatedInterop = await new GameInteropGenerator(request, progress)
                .GenerateAsync(workRoot, cancellationToken);
            var payload = new PayloadSource(
                release.ReleaseRoot,
                generatedInterop.DirectoryPath,
                request.DeploymentPath,
                request.DeploymentPolicies);

            if (request.InputKind == PatchInputKind.Directory)
            {
                ReportStage("Injecting payload into directory");
                PayloadAssembler.InjectDirectory(request.InputPath, payload, progress, cancellationToken);
                ReportStage("Directory ready");
                return new(request.InputPath, null, generatedInterop.UnityVersion, true);
            }

            ReportStage("Packaging APK payload");
            var patchedApk = Path.Combine(workRoot, "patched.apk");
            DirectoryPublisher.CopyFile(request.InputPath, patchedApk, cancellationToken);
            PayloadAssembler.MergeApk(patchedApk, payload, cancellationToken);
            var hash = await ApkPostProcessor.PublishAsync(
                patchedApk,
                request.OutputPath!,
                workRoot,
                postProcessing!,
                progress,
                cancellationToken);
            ReportStage("APK ready");
            return new(request.OutputPath!, hash, generatedInterop.UnityVersion, false);
        }
        finally
        {
            TryDeleteWorkRoot(workRoot);
        }
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

    private void TryDeleteWorkRoot(string workRoot)
    {
        try
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, true);
        }
        catch (Exception exception)
        {
            progress?.Report(new(
                PatcherMessageKind.Warning,
                $"Could not remove temporary directory '{workRoot}': {exception.Message}"));
        }
    }

    private void ReportStage(string message) =>
        progress?.Report(new(PatcherMessageKind.Stage, message));
}
