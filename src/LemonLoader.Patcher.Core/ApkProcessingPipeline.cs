public sealed record ApkProcessingRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public bool AlignApk { get; init; }
    public string? ZipAlignPath { get; init; }
    public string? ApkSignerPath { get; init; }
    public SigningOptions? Signing { get; init; }

    internal PatchRequest Validate() => new PatchRequest
    {
        InputPath = InputPath, OutputPath = OutputPath, AlignApk = AlignApk,
        ZipAlignPath = ZipAlignPath, ApkSignerPath = ApkSignerPath, Signing = Signing
    }.NormalizeAndValidate();
}

public sealed class ApkProcessingPipeline
{
    private readonly PatchRequest request;
    private readonly IProgress<PatcherMessage>? progress;

    public ApkProcessingPipeline(ApkProcessingRequest request, IProgress<PatcherMessage>? progress = null)
    {
        this.request = request.Validate();
        if (this.request.InputKind != PatchInputKind.Apk)
            throw new ArgumentException("APK processing requires a file input.");
        if (!this.request.AlignApk && this.request.Signing is null)
            throw new ArgumentException("APK processing requires alignment or signing.");
        this.progress = progress;
    }

    public async Task<PatchResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = ApkPostProcessor.Resolve(request.PostProcessing);
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
