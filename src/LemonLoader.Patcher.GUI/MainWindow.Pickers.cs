using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow
{
    private async void BrowseFileClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string targetName } ||
            this.FindControl<TextBox>(targetName) is not { } target)
        {
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = GetPickerTitle(targetName),
            AllowMultiple = false,
            FileTypeFilter = GetFileTypes(targetName)
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        target.Text = path;
        if (targetName == nameof(InputPath) &&
            string.IsNullOrWhiteSpace(OutputApkPath.Text))
        {
            OutputApkPath.Text = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}-lemonloader.apk");
        }
    }

    private async void BrowseInputDirectoryClick(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Choose unpacked input directory",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            InputPath.Text = path;
    }

    private async void BrowseOutputApkClick(object? sender, RoutedEventArgs eventArgs)
    {
        var inputPath = InputPath.Text?.Trim();
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
        if (sender is not Button { Tag: string targetName } ||
            this.FindControl<TextBox>(targetName) is not { } target)
        {
            return;
        }
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

    private void AlignmentToggleChanged(object? sender, RoutedEventArgs eventArgs) =>
        AlignmentFields.IsVisible = EnableAlignment.IsChecked == true;

    private void InputPathChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        var directoryInput = !string.IsNullOrWhiteSpace(InputPath.Text) &&
                             Directory.Exists(InputPath.Text.Trim());
        OutputApkLabel.IsEnabled = !directoryInput;
        OutputApkPath.IsEnabled = !directoryInput;
        OutputApkBrowseButton.IsEnabled = !directoryInput;
        PostProcessingSection.IsEnabled = !directoryInput;
        if (!directoryInput)
            return;
        OutputApkPath.Text = string.Empty;
        EnableAlignment.IsChecked = false;
        EnableSigning.IsChecked = false;
    }

    private static string GetPickerTitle(string targetName) => targetName switch
    {
        nameof(InputPath) => "Choose input APK",
        nameof(ReleasePath) => "Choose LemonLoader Release",
        nameof(DeploymentPath) => "Choose deployment directory",
        nameof(UnityLibrariesPath) => "Choose Unity libraries directory",
        nameof(GameAssemblyPath) => "Choose libil2cpp.so",
        nameof(MetadataPath) => "Choose global-metadata.dat",
        nameof(InteropOutputPath) => "Choose Interop output directory",
        nameof(Cpp2IlPath) => "Choose Cpp2IL executable",
        nameof(Il2CppInteropCliPath) => "Choose Il2CppInterop CLI assembly",
        nameof(ZipAlignPath) => "Choose zipalign executable",
        nameof(ApkSignerPath) => "Choose apksigner executable",
        nameof(KeystorePath) => "Choose signing keystore",
        nameof(DependenciesOutputPath) => "Choose Unity dependencies output",
        nameof(DependenciesCachePath) => "Choose Unity dependencies cache",
        _ => "Choose path"
    };

    private static IReadOnlyList<FilePickerFileType> GetFileTypes(string targetName) =>
        targetName switch
        {
            nameof(InputPath) => [ApkFileType],
            nameof(ReleasePath) => [ArchiveFileType],
            nameof(GameAssemblyPath) => [SharedObjectFileType],
            nameof(MetadataPath) => [MetadataFileType],
            nameof(Il2CppInteropCliPath) => [ManagedAssemblyFileType],
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
    private static readonly FilePickerFileType ManagedAssemblyFileType = new("Managed assembly")
    {
        Patterns = ["*.dll"]
    };
    private static readonly FilePickerFileType KeystoreFileType = new("Java keystore")
    {
        Patterns = ["*.jks", "*.keystore"]
    };
}
