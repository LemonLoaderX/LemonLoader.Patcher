using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow : Window
{
    private readonly ConcurrentQueue<PatcherMessage> pendingMessages = new();
    private readonly Queue<string> logLines = new();
    private readonly DispatcherTimer logTimer;
    private CancellationTokenSource? operationCancellation;

    public MainWindow()
    {
        InitializeComponent();
        logTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        logTimer.Tick += (_, _) => DrainProgress();
        logTimer.Start();
        WorkspaceTabs.SelectionChanged += (_, _) =>
        {
            RunButton.Content = WorkspaceTabs.SelectedIndex == 0
                ? "Patch"
                : "Restore dependencies";
            ValidationText.Text = string.Empty;
        };
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        logTimer.Stop();
        operationCancellation?.Cancel();
        base.OnClosed(eventArgs);
    }

    private async void RunClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (operationCancellation is not null)
            return;

        ResetLog();
        operationCancellation = new CancellationTokenSource();
        SetRunning(true);
        var progress = new QueueProgress(pendingMessages);
        try
        {
            if (WorkspaceTabs.SelectedIndex == 0)
                await RunPatchAsync(progress, operationCancellation.Token);
            else
                await RestoreDependenciesAsync(progress, operationCancellation.Token);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Cancelled";
            AppendLog("cancelled", "No output was published.");
            RefreshLog();
        }
        catch (ArgumentException exception)
        {
            ShowFailure("Check required fields", "input", exception.Message);
        }
        catch (Exception exception)
        {
            ShowFailure("Task failed", "error", GetUsefulMessage(exception));
        }
        finally
        {
            DrainProgress();
            operationCancellation.Dispose();
            operationCancellation = null;
            SetRunning(false);
        }
    }

    private async Task RunPatchAsync(
        IProgress<PatcherMessage> progress,
        CancellationToken cancellationToken)
    {
        var result = await new ApkPatchPipeline(BuildPatchRequest(), progress)
            .RunAsync(cancellationToken);
        DrainProgress();
        StatusText.Text = result.ModifiedInPlace ? "Directory ready" : "APK ready";
        AppendLog("result", result.OutputPath);
        if (result.Sha256 is not null)
            AppendLog("sha256", result.Sha256);
        RefreshLog();
    }

    private async Task RestoreDependenciesAsync(
        IProgress<PatcherMessage> progress,
        CancellationToken cancellationToken)
    {
        var result = await UnityDependenciesPipeline.RunAsync(
            BuildDependenciesRequest(),
            progress,
            cancellationToken);
        DrainProgress();
        StatusText.Text = $"Restored {result.AssemblyCount} Unity assemblies";
        AppendLog("result", result.OutputPath);
        AppendLog("source", result.Source);
        RefreshLog();
    }

    private void CancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (operationCancellation is null)
            return;
        StatusText.Text = "Cancelling...";
        operationCancellation.Cancel();
    }

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        WorkspaceTabs.IsEnabled = !running;
        OperationProgress.IsVisible = running;
        if (running)
        {
            StatusText.Text = WorkspaceTabs.SelectedIndex == 0
                ? "Preparing patch"
                : "Preparing Unity dependency restore";
        }
    }

    private void ShowFailure(string status, string label, string message)
    {
        StatusText.Text = status;
        ValidationText.Text = message;
        AppendLog(label, message);
        RefreshLog();
    }
}
