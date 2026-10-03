using System.Collections.Concurrent;

internal sealed class ToolOutputProgress(ConcurrentQueue<PatcherMessage> messages) : IProgress<PatcherMessage>
{
    public void Report(PatcherMessage message) => messages.Enqueue(message);
}
