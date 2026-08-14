using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonLoader.Patcher.Gui;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void PatchClick(object? sender, RoutedEventArgs eventArgs)
    {
        var button = (Button)sender!;
        button.IsEnabled = false;
        Status.Text = "Building Interop and APK...";
        try
        {
            var arguments = new List<string> { "patch", "--apk", ApkPath.Text!, "--output", OutputPath.Text! };
            Add("--release", ReleasePath.Text); Add("--libil2cpp", LibIl2CppPath.Text); Add("--metadata", MetadataPath.Text);
            Add("--unity-version", UnityVersion.Text); Add("--unity-libs", UnityLibraries.Text);
            Add("--interop-output", InteropOutput.Text); Add("--android-sdk", AndroidSdk.Text);
            Add("--keystore", Keystore.Text); Add("--ks-alias", KeyAlias.Text); Add("--ks-pass", StorePassword.Text);
            AddMany("--deployment", DeploymentPaths.Text);
            AddMany("--mod", ModPaths.Text);
            AddMany("--plugin", PluginPaths.Text);
            AddMany("--user-lib", UserLibPaths.Text);
            AddMany("--user-data", UserDataPaths.Text);
            if (Align.IsChecked == true) arguments.Add("--align");
            var result = await PatcherApplication.RunAsync(arguments.ToArray());
            Status.Text = result == 0 ? $"Finished: {OutputPath.Text}" : "Patching failed. See the console log.";

            void Add(string option, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) arguments.AddRange([option, value]);
            }
            void AddMany(string option, string? values)
            {
                foreach (var value in (values ?? "").Split(
                             ';',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    arguments.AddRange([option, value]);
                }
            }
        }
        catch (Exception exception) { Status.Text = exception.Message; }
        finally { button.IsEnabled = true; }
    }
}
