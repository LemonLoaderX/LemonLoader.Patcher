using System.Collections.Concurrent;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow
{
    private const int MaximumLogLines = 1200;

    private void ResetLog()
    {
        ValidationText.Text = string.Empty;
        OperationLog.Text = string.Empty;
        logLines.Clear();
        while (pendingMessages.TryDequeue(out _))
        {
        }
    }

    private void DrainProgress()
    {
        var changed = false;
        while (pendingMessages.TryDequeue(out var message))
        {
            if (message.Kind == PatcherMessageKind.Stage)
                StatusText.Text = message.Text;
            AppendLog(message.Kind switch
            {
                PatcherMessageKind.Stage => "stage",
                PatcherMessageKind.ToolOutput => "tool",
                PatcherMessageKind.Warning => "warning",
                _ => "info"
            }, message.Text);
            changed = true;
        }
        if (changed)
            RefreshLog();
    }

    private void AppendLog(string label, string message)
    {
        logLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {label,-7} {message}");
        while (logLines.Count > MaximumLogLines)
            logLines.Dequeue();
    }

    private void RefreshLog()
    {
        OperationLog.Text = string.Join(Environment.NewLine, logLines);
        OperationLog.CaretIndex = OperationLog.Text.Length;
    }

    private static string GetUsefulMessage(Exception exception)
    {
        if (exception is not AggregateException aggregate)
            return exception.Message;
        return string.Join(
            "; ",
            aggregate.Flatten().InnerExceptions
                .Select(GetUsefulMessage)
                .Distinct(StringComparer.Ordinal));
    }

    private sealed class QueueProgress(ConcurrentQueue<PatcherMessage> messages)
        : IProgress<PatcherMessage>
    {
        public void Report(PatcherMessage value) => messages.Enqueue(value);
    }
}
