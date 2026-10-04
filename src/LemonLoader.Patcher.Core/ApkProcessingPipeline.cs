namespace LemonLoader.Patcher.Core;

public sealed record ApkProcessingRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public ApkPostProcessingOptions PostProcessing { get; init; } = new();

    public ApkProcessingRequest NormalizeAndValidate()
    {
        var options = PostProcessing.NormalizeAndValidate();
        var request = this with
        {
            InputPath = RequestPaths.Optional(InputPath) ?? throw new ArgumentException("Input APK is required."),
            OutputPath = RequestPaths.Optional(OutputPath) ?? throw new ArgumentException("Output APK is required."),
            PostProcessing = options
        };
        RequestPaths.RequireFile(request.InputPath, "Input APK");
        if (!options.Align && options.Signing is null)
            throw new ArgumentException("APK processing requires alignment or signing.");
        RequestPaths.ValidateOutputFile(request.OutputPath, request.InputPath,
            options.ZipAlignPath, options.ApkSignerPath, options.Signing?.KeystorePath);
        return request;
    }
}

public sealed class ApkProcessingPipeline
{
    private readonly ApkProcessingRequest request;
    private readonly IProgress<PatcherMessage>? progress;

    public ApkProcessingPipeline(ApkProcessingRequest request, IProgress<PatcherMessage>? progress = null)
    {
        this.request = request.NormalizeAndValidate();
        this.progress = progress;
    }

    public async Task<PatchResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = ApkPostProcessor.Resolve(request.PostProcessing);
        RequestPaths.ValidateOutputFile(request.OutputPath!, request.InputPath,
            options.ZipAlignPath, options.ApkSignerPath, options.Signing?.KeystorePath);
        using (var apk = System.IO.Compression.ZipFile.OpenRead(request.InputPath))
            ArchiveSafety.Validate(apk);
        using var work = new PatchWorkspace(progress);
        var staged = Path.Combine(work.Root, "input.apk");
        DirectoryPublisher.CopyFile(request.InputPath, staged, cancellationToken);
        var hash = await ApkPostProcessor.PublishAsync(staged, request.OutputPath!, work.Root,
            options, progress, cancellationToken);
        return new(request.OutputPath!, hash, null, false);
    }
}
