using System.Collections.Concurrent;
using Avalonia.Threading;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow
{
    private const int MaximumLogLines = 1200;
    private bool scrollPending;

    private void ResetLog()
    {
        ValidationText.Text = string.Empty;
        logLines.Clear();
        while (pendingMessages.TryDequeue(out _))
        {
        }
    }

    private void DrainProgress(int maximum = int.MaxValue)
    {
        var changed = false;
        while (maximum-- > 0 && pendingMessages.TryDequeue(out var message))
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
        logLines.Add($"[{DateTime.Now:HH:mm:ss}] {label,-7} {message}");
        while (logLines.Count > MaximumLogLines)
            logLines.RemoveAt(0);
    }

    private void RefreshLog()
    {
        if (AutoScroll.IsChecked != true || logLines.Count == 0 || scrollPending)
            return;
        scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            scrollPending = false;
            if (AutoScroll.IsChecked == true && logLines.Count > 0)
                OperationLog.ScrollIntoView(logLines[^1]);
        }, DispatcherPriority.Loaded);
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
        public void Report(PatcherMessage value)
        {
            messages.Enqueue(value);
            while (messages.Count > MaximumLogLines * 2)
                messages.TryDequeue(out _);
        }
    }
}
