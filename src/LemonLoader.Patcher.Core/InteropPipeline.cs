namespace LemonLoader.Patcher.Core;

public sealed record InteropResult(string OutputPath, string UnityVersion, int AssemblyCount);

public sealed class InteropPipeline
{
    private readonly InteropRequest request;
    private readonly IProgress<PatcherMessage>? progress;

    public InteropPipeline(InteropRequest request, IProgress<PatcherMessage>? progress = null)
    {
        this.request = request.NormalizeAndValidate();
        if (this.request.OutputPath is null)
            throw new ArgumentException("Interop generation requires an output directory.");
        this.progress = progress;
    }

    public async Task<InteropResult> RunAsync(CancellationToken cancellationToken = default)
    {
        using var work = new PatchWorkspace(progress);
        var result = await new GameInteropGenerator(request, progress).GenerateAsync(work.Root, cancellationToken);
        return new(request.OutputPath!, result.UnityVersion, Directory.GetFiles(result.DirectoryPath, "*.dll").Length);
    }
}
