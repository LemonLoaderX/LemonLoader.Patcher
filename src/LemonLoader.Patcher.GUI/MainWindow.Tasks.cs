using Avalonia.Controls;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow
{
    private void UpdateTaskMode()
    {
        var mode = OperationMode.SelectedIndex;
        var injection = mode is 0 or 2;
        var generation = mode is 0 or 1;
        foreach (var control in new Control[] { ReleaseLabel, ReleasePath, ReleaseBrowseButton, RuntimeLabel, RuntimeVariant, DeploymentSection })
            control.IsVisible = injection;
        ExistingInteropRow.IsVisible = mode == 2;
        InteropExportRow.IsVisible = generation;
        InteropOutputPath.Watermark = mode == 1 ? "Required output directory" : "Optional export directory";
        InteropSection.IsVisible = generation;
        if (mode == 1) InteropSection.IsExpanded = true;
        foreach (var control in new Control[] { OutputApkLabel, OutputApkPath, OutputApkBrowseButton })
            control.IsVisible = mode != 1;
        PostProcessingSection.IsVisible = mode != 1;
        if (mode == 3) PostProcessingSection.IsExpanded = true;
        RunButton.Content = WorkspaceTabs.SelectedIndex == 1 ? "Restore dependencies" : mode switch
        {
            1 => "Generate Interop", 2 => "Inject", 3 => "Process APK", _ => "Patch"
        };
    }
}
