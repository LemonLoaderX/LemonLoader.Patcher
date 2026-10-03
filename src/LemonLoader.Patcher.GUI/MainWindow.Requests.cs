using Avalonia.Controls;

namespace LemonLoader.Patcher.GUI;

public sealed partial class MainWindow
{
    private PatchRequest BuildPatchRequest()
    {
        var injecting = OperationMode.SelectedIndex == 2;
        var inputPath = Required(InputPath, "Choose an input APK or directory.");
        var directoryInput = Directory.Exists(inputPath);
        var profile = (DeploymentProfile.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var rules = (DeploymentPolicyRules.Text ?? string.Empty)
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var signing = BuildSigning();

        return new()
        {
            InputPath = inputPath,
            OutputPath = directoryInput
                ? Optional(OutputApkPath)
                : Required(OutputApkPath, "Choose an output APK."),
            ReleasePath = Optional(ReleasePath),
            RuntimeVariant = (RuntimeVariant.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            DeploymentPath = Optional(DeploymentPath),
            DeploymentPolicies = DeploymentPolicyOptions.Create(profile, rules),
            InteropInputPath = injecting ? Required(InteropInputPath, "Choose existing Interop DLLs.") : null,
            UnityVersion = injecting ? null : Optional(UnityVersion),
            UnityLibrariesPath = injecting ? null : Optional(UnityLibrariesPath),
            GameAssemblyPath = injecting ? null : Optional(GameAssemblyPath),
            MetadataPath = injecting ? null : Optional(MetadataPath),
            InteropOutputPath = injecting ? null : Optional(InteropOutputPath),
            Cpp2IlPath = injecting ? null : Optional(Cpp2IlPath),
            Il2CppInteropCliPath = injecting ? null : Optional(Il2CppInteropCliPath),
            AlignApk = EnableAlignment.IsChecked == true,
            ZipAlignPath = EnableAlignment.IsChecked == true ? Optional(ZipAlignPath) : null,
            ApkSignerPath = signing is not null ? Optional(ApkSignerPath) : null,
            Signing = signing
        };
    }

    private SigningOptions? BuildSigning() => EnableSigning.IsChecked == true
            ? new SigningOptions(
                Required(KeystorePath, "Choose a keystore."),
                Required(StorePassword, "Enter the keystore password."),
                Required(KeyAlias, "Enter the key alias."),
                Optional(KeyPassword))
            : null;

    private InteropRequest BuildInteropRequest() => new()
    {
        InputPath = Optional(InputPath), OutputPath = Required(InteropOutputPath, "Choose an Interop output directory."),
        UnityVersion = Optional(UnityVersion), UnityLibrariesPath = Optional(UnityLibrariesPath),
        GameAssemblyPath = Optional(GameAssemblyPath), MetadataPath = Optional(MetadataPath),
        Cpp2IlPath = Optional(Cpp2IlPath), Il2CppInteropCliPath = Optional(Il2CppInteropCliPath)
    };

    private ApkProcessingRequest BuildProcessingRequest()
    {
        var signing = BuildSigning();
        return new()
        {
            InputPath = Required(InputPath, "Choose an input APK."),
            OutputPath = Required(OutputApkPath, "Choose an output APK."),
            AlignApk = EnableAlignment.IsChecked == true,
            ZipAlignPath = EnableAlignment.IsChecked == true ? Optional(ZipAlignPath) : null,
            ApkSignerPath = signing is not null ? Optional(ApkSignerPath) : null,
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

    private static string Required(TextBox input, string message)
    {
        var value = input.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        input.Focus();
        throw new ArgumentException(message);
    }

    private static string? Optional(TextBox input) =>
        string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
}
