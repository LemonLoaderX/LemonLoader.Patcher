using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow
{
    private void SetResult(string path)
    {
        resultPath = path;
        OpenResultButton.IsEnabled = true;
    }

    private void OpenResultClick(object? sender, RoutedEventArgs args)
    {
        if (resultPath is null)
            return;
        try
        {
            var directory = Directory.Exists(resultPath) ? resultPath : Path.GetDirectoryName(resultPath)!;
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception) { ShowFailure("Could not open output", "error", exception.Message); }
    }

    private async void CopyLogClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, logLines));
        }
        catch (Exception exception) { ShowFailure("Could not copy log", "error", exception.Message); }
    }

    private async void SaveLogClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var text = string.Join(Environment.NewLine, logLines);
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Save task log", SuggestedFileName = "patcher.log", DefaultExtension = "log"
            });
            if (file is null)
                return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
        }
        catch (Exception exception) { ShowFailure("Could not save log", "error", exception.Message); }
    }
}
