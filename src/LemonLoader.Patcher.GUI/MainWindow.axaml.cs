using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow : Window
{
    private const int MaximumLogLines = 1200;
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
                ? "Patch APK"
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

    private async void BrowseFileClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string targetName })
            return;
        var target = this.FindControl<TextBox>(targetName);
        if (target is null)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = GetPickerTitle(targetName),
            AllowMultiple = false,
            FileTypeFilter = GetFileTypes(targetName)
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
            return;
        target.Text = path;
        if (targetName == nameof(InputApkPath) &&
            string.IsNullOrWhiteSpace(OutputApkPath.Text))
        {
            OutputApkPath.Text = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}-lemonloader.apk");
        }
    }

    private async void BrowseOutputApkClick(object? sender, RoutedEventArgs eventArgs)
    {
        var inputPath = InputApkPath.Text?.Trim();
        var suggestedName = string.IsNullOrWhiteSpace(inputPath)
            ? "game-lemonloader.apk"
            : $"{Path.GetFileNameWithoutExtension(inputPath)}-lemonloader.apk";
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Choose output APK",
            SuggestedFileName = suggestedName,
            DefaultExtension = "apk",
            FileTypeChoices = [ApkFileType]
        });
        if (file?.TryGetLocalPath() is { } path)
            OutputApkPath.Text = path;
    }

    private async void BrowseFolderClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string targetName })
            return;
        var target = this.FindControl<TextBox>(targetName);
        if (target is null)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = GetPickerTitle(targetName),
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            target.Text = path;
    }

    private void SigningToggleChanged(object? sender, RoutedEventArgs eventArgs) =>
        SigningFields.IsVisible = EnableSigning.IsChecked == true;

    private async void RunClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (operationCancellation is not null)
            return;

        ValidationText.Text = string.Empty;
        OperationLog.Text = string.Empty;
        logLines.Clear();
        while (pendingMessages.TryDequeue(out _))
        {
        }
        operationCancellation = new CancellationTokenSource();
        SetRunning(true);
        var progress = new QueueProgress(pendingMessages);
        try
        {
            if (WorkspaceTabs.SelectedIndex == 0)
            {
                var result = await new ApkPatchPipeline(BuildPatchRequest(), progress)
                    .RunAsync(operationCancellation.Token);
                DrainProgress();
                StatusText.Text = "APK ready";
                AppendLog("result", result.OutputApkPath);
                AppendLog("sha256", result.Sha256);
                RefreshLog();
            }
            else
            {
                var result = await UnityDependenciesPipeline.RunAsync(
                    BuildDependenciesRequest(),
                    progress,
                    operationCancellation.Token);
                DrainProgress();
                StatusText.Text = $"Restored {result.AssemblyCount} Unity assemblies";
                AppendLog("result", result.OutputPath);
                AppendLog("source", result.Source);
                RefreshLog();
            }
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Cancelled";
            AppendLog("cancelled", "No output was published.");
            RefreshLog();
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = "Check required fields";
            ValidationText.Text = exception.Message;
            AppendLog("input", exception.Message);
            RefreshLog();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Task failed";
            ValidationText.Text = GetUsefulMessage(exception);
            AppendLog("error", GetUsefulMessage(exception));
            RefreshLog();
        }
        finally
        {
            DrainProgress();
            operationCancellation.Dispose();
            operationCancellation = null;
            SetRunning(false);
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (operationCancellation is null)
            return;
        StatusText.Text = "Cancelling...";
        operationCancellation.Cancel();
    }

    private PatchRequest BuildPatchRequest()
    {
        var profile = (DeploymentProfile.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var rules = (DeploymentPolicyRules.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SigningOptions? signing = null;
        if (EnableSigning.IsChecked == true)
        {
            signing = new(
                Required(KeystorePath, "Choose a keystore."),
                Required(StorePassword, "Enter the keystore password."),
                Required(KeyAlias, "Enter the key alias."),
                Optional(KeyPassword));
        }

        return new PatchRequest
        {
            InputApkPath = Required(InputApkPath, "Choose an input APK."),
            OutputApkPath = Required(OutputApkPath, "Choose an output APK."),
            ReleasePath = Optional(ReleasePath),
            DeploymentPath = Optional(DeploymentPath),
            DeploymentPolicies = DeploymentPolicyOptions.Create(profile, rules),
            UnityVersion = Optional(UnityVersion),
            UnityLibrariesPath = Optional(UnityLibrariesPath),
            GameAssemblyPath = Optional(GameAssemblyPath),
            MetadataPath = Optional(MetadataPath),
            InteropOutputPath = Optional(InteropOutputPath),
            Cpp2IlPath = Optional(Cpp2IlPath),
            AndroidSdkRoot = Optional(AndroidSdkPath),
            Signing = signing
        };
    }

    private UnityDependenciesRequest BuildDependenciesRequest() => new()
    {
        UnityVersion = Required(
            DependenciesUnityVersion,
            "Enter the full Unity version."),
        OutputPath = Required(
            DependenciesOutputPath,
            "Choose the Unity dependencies output directory."),
        CachePath = Optional(DependenciesCachePath)
    };

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        WorkspaceTabs.IsEnabled = !running;
        OperationProgress.IsVisible = running;
        if (running)
            StatusText.Text = WorkspaceTabs.SelectedIndex == 0
                ? "Preparing APK patch"
                : "Preparing Unity dependency restore";
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
        var line = $"[{DateTime.Now:HH:mm:ss}] {label,-7} {message}";
        logLines.Enqueue(line);
        while (logLines.Count > MaximumLogLines)
            logLines.Dequeue();
    }

    private void RefreshLog()
    {
        OperationLog.Text = string.Join(Environment.NewLine, logLines);
        OperationLog.CaretIndex = OperationLog.Text.Length;
    }

    private static string Required(TextBox input, string message)
    {
        var value = input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            input.Focus();
            throw new ArgumentException(message);
        }
        return value;
    }

    private static string? Optional(TextBox input) =>
        string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();

    private static string GetUsefulMessage(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            return string.Join(
                "; ",
                aggregate.Flatten().InnerExceptions
                    .Select(GetUsefulMessage)
                    .Distinct(StringComparer.Ordinal));
        }
        return exception.Message;
    }

    private static string GetPickerTitle(string targetName) => targetName switch
    {
        nameof(InputApkPath) => "Choose input APK",
        nameof(ReleasePath) => "Choose LemonLoader Release",
        nameof(DeploymentPath) => "Choose deployment directory",
        nameof(UnityLibrariesPath) => "Choose Unity libraries directory",
        nameof(GameAssemblyPath) => "Choose libil2cpp.so",
        nameof(MetadataPath) => "Choose global-metadata.dat",
        nameof(InteropOutputPath) => "Choose Interop output directory",
        nameof(Cpp2IlPath) => "Choose Cpp2IL executable",
        nameof(AndroidSdkPath) => "Choose Android SDK directory",
        nameof(KeystorePath) => "Choose signing keystore",
        nameof(DependenciesOutputPath) => "Choose Unity dependencies output",
        nameof(DependenciesCachePath) => "Choose Unity dependencies cache",
        _ => "Choose path"
    };

    private static IReadOnlyList<FilePickerFileType> GetFileTypes(string targetName) =>
        targetName switch
        {
            nameof(InputApkPath) => [ApkFileType],
            nameof(ReleasePath) => [ArchiveFileType],
            nameof(GameAssemblyPath) => [SharedObjectFileType],
            nameof(MetadataPath) => [MetadataFileType],
            nameof(KeystorePath) => [KeystoreFileType],
            _ => [FilePickerFileTypes.All]
        };

    private static readonly FilePickerFileType ApkFileType = new("Android APK")
    {
        Patterns = ["*.apk"]
    };
    private static readonly FilePickerFileType ArchiveFileType = new("ZIP archive")
    {
        Patterns = ["*.zip"]
    };
    private static readonly FilePickerFileType SharedObjectFileType = new("Android shared object")
    {
        Patterns = ["*.so"]
    };
    private static readonly FilePickerFileType MetadataFileType = new("IL2CPP metadata")
    {
        Patterns = ["*.dat"]
    };
    private static readonly FilePickerFileType KeystoreFileType = new("Java keystore")
    {
        Patterns = ["*.jks", "*.keystore"]
    };

    private sealed class QueueProgress(ConcurrentQueue<PatcherMessage> messages)
        : IProgress<PatcherMessage>
    {
        public void Report(PatcherMessage value) => messages.Enqueue(value);
    }
}
