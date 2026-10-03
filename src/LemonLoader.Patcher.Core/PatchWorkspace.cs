internal sealed class PatchWorkspace : IDisposable
{
    private readonly IProgress<PatcherMessage>? progress;
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"lemonloader-patcher-{Guid.NewGuid():N}");

    internal PatchWorkspace(IProgress<PatcherMessage>? progress)
    {
        this.progress = progress;
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            progress?.Report(new(PatcherMessageKind.Warning, $"Could not remove temporary directory '{Root}': {exception.Message}"));
        }
    }
}
