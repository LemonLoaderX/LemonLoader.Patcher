using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using LemonLoader.Patcher.GUI;
using System.Reflection;

AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var window = new MainWindow();
window.Show();
Dispatcher.UIThread.RunJobs();
var input = window.FindControl<TextBox>("InputPath")!;
var output = window.FindControl<TextBox>("OutputApkPath")!;
input.Text = Path.Combine(Path.GetTempPath(), "first.apk");
Dispatcher.UIThread.RunJobs();
if (!output.Text!.EndsWith("first-lemonloader.apk")) throw new Exception("Missing output suggestion.");
input.Text = Path.Combine(Path.GetTempPath(), "second.apk");
Dispatcher.UIThread.RunJobs();
if (!output.Text!.EndsWith("second-lemonloader.apk")) throw new Exception("Stale suggested output.");
output.Text = Path.Combine(Path.GetTempPath(), "explicit.apk");
input.Text = Path.Combine(Path.GetTempPath(), "third.apk");
Dispatcher.UIThread.RunJobs();
if (!output.Text!.EndsWith("explicit.apk")) throw new Exception("Explicit output was overwritten.");
var append = typeof(MainWindow).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
for (int i = 0; i < 10000; i++) append.Invoke(window, ["tool", "Tool output " + i]);
var list = window.FindControl<ListBox>("OperationLog")!;
if (list.Items.Count != 1200) throw new Exception("Log retained unbounded output.");
typeof(MainWindow).GetMethod("RunClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, new Avalonia.Interactivity.RoutedEventArgs()]);
Dispatcher.UIThread.RunJobs();
if (list.Items.Count != 1200 || window.FindControl<TextBlock>("StatusText")!.Text != "Check required fields")
    throw new Exception("Invalid request discarded prior log or final status.");
foreach (var size in new[] { new Size(1120,780), new Size(900,640) })
{
    window.Width = size.Width; window.Height = size.Height;
    window.Measure(size); window.Arrange(new Rect(size));
    Dispatcher.UIThread.RunJobs();
    foreach (var name in new[] { "RunButton", "CancelButton", "OpenResultButton" })
    {
        var control = window.FindControl<Button>(name)!;
        var position = control.TranslatePoint(default, window)!.Value;
        if (position.X < 0 || position.Y < 0 || position.X + control.Bounds.Width > size.Width ||
            position.Y + control.Bounds.Height > size.Height)
            throw new Exception("Command outside window: " + name);
    }
}
var mode = window.FindControl<ComboBox>("OperationMode")!;
mode.SelectedIndex = 1;
Dispatcher.UIThread.RunJobs();
if (window.FindControl<TextBox>("ReleasePath")!.IsVisible || window.FindControl<TextBox>("OutputApkPath")!.IsVisible ||
    !window.FindControl<TextBox>("InteropOutputPath")!.IsVisible || window.FindControl<Button>("RunButton")!.Content?.ToString() != "Generate Interop")
    throw new Exception("Generation mode exposes injection-only fields.");
mode.SelectedIndex = 2;
Dispatcher.UIThread.RunJobs();
if (!window.FindControl<Grid>("ExistingInteropRow")!.IsVisible || window.FindControl<Expander>("InteropSection")!.IsVisible)
    throw new Exception("Injection mode exposes generator options.");
mode.SelectedIndex = 3;
Dispatcher.UIThread.RunJobs();
if (window.FindControl<TextBox>("ReleasePath")!.IsVisible || window.FindControl<Grid>("ExistingInteropRow")!.IsVisible ||
    window.FindControl<Expander>("InteropSection")!.IsVisible || !window.FindControl<Expander>("PostProcessingSection")!.IsVisible)
    throw new Exception("APK processing mode exposes generation/injection inputs.");
window.Close();
Console.WriteLine("PASS GUI suggested/custom paths, bounded log, validation preserves evidence, command layout.");
