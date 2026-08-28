using System.IO.Compression;
using System.Security.Cryptography;

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
            var releaseRoot = await ResolveReleaseAsync(workRoot, cancellationToken);
            var generatedInterop = await new GameInteropGenerator(request, progress)
                .GenerateAsync(workRoot, cancellationToken);
            var payload = new PayloadSource(
                releaseRoot,
                generatedInterop.DirectoryPath,
                request.DeploymentPath,
                request.DeploymentPolicies);

            if (request.InputKind == PatchInputKind.Directory)
            {
                ReportStage("Injecting payload into directory");
                PayloadAssembler.InjectDirectory(request.InputPath, payload, progress);
                ReportStage("Directory ready");
                return new(request.InputPath, null, generatedInterop.UnityVersion, true);
            }

            ReportStage("Packaging APK payload");
            var patchedApk = Path.Combine(workRoot, "patched.apk");
            File.Copy(request.InputPath, patchedApk, true);
            PayloadAssembler.MergeApk(patchedApk, payload);
            await ApkPostProcessor.PublishAsync(
                patchedApk,
                request.OutputPath!,
                workRoot,
                postProcessing!,
                progress,
                cancellationToken);
            var hash = await ComputeHashAsync(request.OutputPath!, cancellationToken);
            ReportStage("APK ready");
            return new(request.OutputPath!, hash, generatedInterop.UnityVersion, false);
        }
        finally
        {
            TryDeleteWorkRoot(workRoot);
        }
    }

    private async Task<string> ResolveReleaseAsync(
        string workRoot,
        CancellationToken cancellationToken)
    {
        ReportStage("Resolving LemonLoader Release");
        var releaseArchive = request.ReleasePath ?? await ReleaseResolver.ResolveLatestAsync(
            request.ToolCacheRoot,
            progress,
            cancellationToken);
        if (!File.Exists(releaseArchive))
        {
            throw new FileNotFoundException(
                $"LemonLoader Release was not found at '{releaseArchive}'.");
        }
        var releaseRoot = Path.Combine(workRoot, "release");
        ZipFile.ExtractToDirectory(releaseArchive, releaseRoot);
        ReleaseValidator.Validate(releaseRoot);
        return releaseRoot;
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

    private static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken))
            .ToLowerInvariant();
    }

    private void ReportStage(string message) =>
        progress?.Report(new(PatcherMessageKind.Stage, message));
}
