using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow : Window
{
    private readonly ConcurrentQueue<PatcherMessage> pendingMessages = new();
    private readonly ObservableCollection<string> logLines = new();
    private readonly DispatcherTimer logTimer;
    private readonly Stopwatch operationTimer = new();
    private CancellationTokenSource? operationCancellation;
    private string? resultPath;

    public MainWindow()
    {
        InitializeComponent();
        logTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        OperationLog.ItemsSource = logLines;
        logTimer.Tick += (_, _) =>
        {
            DrainProgress(200);
            if (operationTimer.IsRunning)
                ElapsedText.Text = operationTimer.Elapsed.ToString(@"hh\:mm\:ss");
        };
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

        ApkPatchPipeline? pipeline = null;
        UnityDependenciesRequest? dependencies = null;
        try
        {
            if (WorkspaceTabs.SelectedIndex == 0)
                pipeline = new ApkPatchPipeline(BuildPatchRequest(), new QueueProgress(pendingMessages));
            else
                dependencies = BuildDependenciesRequest();
        }
        catch (Exception exception)
        {
            ShowFailure("Check required fields", "input", GetUsefulMessage(exception));
            return;
        }
        ResetLog();
        resultPath = null;
        OpenResultButton.IsEnabled = false;
        operationTimer.Restart();
        operationCancellation = new CancellationTokenSource();
        SetRunning(true);
        var progress = new QueueProgress(pendingMessages);
        try
        {
            if (WorkspaceTabs.SelectedIndex == 0)
                await RunPatchAsync(pipeline!, operationCancellation.Token);
            else
                await RestoreDependenciesAsync(dependencies!, progress, operationCancellation.Token);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            DrainProgress();
            StatusText.Text = "Cancelled";
            AppendLog("cancelled", "The patch was not committed. A requested Interop export may already be available.");
            RefreshLog();
        }
        catch (ArgumentException exception)
        {
            DrainProgress();
            ShowFailure("Check required fields", "input", exception.Message);
        }
        catch (Exception exception)
        {
            DrainProgress();
            ShowFailure("Task failed", "error", GetUsefulMessage(exception));
        }
        finally
        {
            operationTimer.Stop();
            ElapsedText.Text = operationTimer.Elapsed.ToString(@"hh\:mm\:ss");
            operationCancellation.Dispose();
            operationCancellation = null;
            SetRunning(false);
        }
    }

    private async Task RunPatchAsync(
        ApkPatchPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => pipeline.RunAsync(cancellationToken), cancellationToken);
        DrainProgress();
        StatusText.Text = result.ModifiedInPlace ? "Directory ready" : "APK ready";
        SetResult(result.OutputPath);
        AppendLog("result", result.OutputPath);
        if (result.Sha256 is not null)
            AppendLog("sha256", result.Sha256);
        RefreshLog();
    }

    private async Task RestoreDependenciesAsync(
        UnityDependenciesRequest request,
        IProgress<PatcherMessage> progress,
        CancellationToken cancellationToken)
    {
        var result = await Task.Run(() => UnityDependenciesPipeline.RunAsync(
            request, progress, cancellationToken), cancellationToken);
        DrainProgress();
        StatusText.Text = $"Restored {result.AssemblyCount} Unity assemblies";
        SetResult(result.OutputPath);
        AppendLog("result", result.OutputPath);
        AppendLog("source", result.Source);
        RefreshLog();
    }

    private void CancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (operationCancellation is null)
            return;
        StatusText.Text = "Cancelling...";
        CancelButton.IsEnabled = false;
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
