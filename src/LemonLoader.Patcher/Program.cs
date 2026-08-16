using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await PatcherApplication.RunAsync(args, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

public static class PatcherApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 ||
            args[0] is "--help" or "-h" ||
            (args.Length == 2 && args[1] is "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            switch (args[0])
            {
                case "patch":
                    var patchOptions = PatchOptions.Parse(args[1..]);
                    await new ApkPatchPipeline(patchOptions).RunAsync(cancellationToken);
                    return 0;
                case "unity-dependencies":
                    var dependencyOptions = UnityDependenciesOptions.Parse(args[1..]);
                    var cacheRoot = dependencyOptions.CachePath ?? Path.Combine(
                        Path.GetDirectoryName(dependencyOptions.OutputPath)!,
                        ".tools",
                        "UnityDependencies");
                    var resolution = await UnityDependenciesResolver.ResolveAsync(
                        cacheRoot,
                        dependencyOptions.UnityVersion,
                        cancellationToken);
                    UnityDependenciesResolver.Publish(resolution, dependencyOptions.OutputPath);
                    Console.WriteLine(
                        $"Published {resolution.AssemblyCount} Unity base libraries: {dependencyOptions.OutputPath}");
                    return 0;
                default:
                    Console.Error.WriteLine("Unknown command. Use --help for available commands.");
                    return 2;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        LemonLoader.Patcher

        patch --apk <original.apk> [--release <LemonLoader-Android.zip>] --output <mod.apk>
              [--libil2cpp <libil2cpp.so> --metadata <global-metadata.dat>]
              [--unity-version <version>] [--unity-libs <directory>]
              [--interop-output <directory>]
              [--cpp2il <path>] [--deployment <directory>]...
              [--mod <path>]... [--plugin <path>]...
              [--user-lib <path>]... [--user-data <path>]...
              [--deployment-profile <development|production|locked>]
              [--deployment-policy <path-or-directory/**=policy>]...
              [--android-sdk <path>] [--align]
              [--keystore <path> --ks-pass <password> --ks-alias <alias> [--key-pass <password>]]

        unity-dependencies --unity-version <version> --output <directory>
                           [--cache <directory>]

        The APK is read and updated as a ZIP. apktool is not required.
        """);
}

public sealed record UnityDependenciesOptions(
    string UnityVersion,
    string OutputPath,
    string? CachePath)
{
    public static UnityDependenciesOptions Parse(string[] args)
    {
        var values = ParseNamedValues(
            args,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "--unity-version", "--output", "--cache"
            });
        if (!values.TryGetValue("--unity-version", out var unityVersion) ||
            string.IsNullOrWhiteSpace(unityVersion))
        {
            throw new ArgumentException("Missing required option '--unity-version'.");
        }
        if (!values.TryGetValue("--output", out var output))
            throw new ArgumentException("Missing required option '--output'.");
        return new(
            unityVersion,
            Path.GetFullPath(output),
            values.TryGetValue("--cache", out var cache) ? Path.GetFullPath(cache) : null);
    }

    internal static Dictionary<string, string> ParseNamedValues(
        string[] args,
        IReadOnlySet<string> supportedOptions)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!supportedOptions.Contains(name))
                throw new ArgumentException($"Unknown option '{name}'.");
            if (index + 1 == args.Length)
                throw new ArgumentException($"Missing value for option '{name}'.");
            if (!values.TryAdd(name, args[++index]))
                throw new ArgumentException($"Option '{name}' was supplied more than once.");
        }
        return values;
    }
}

public sealed record PatchOptions(
    string ApkPath,
    string? ReleasePath,
    string OutputPath,
    string? LibIl2CppPath,
    string? MetadataPath,
    string? UnityVersion,
    string? UnityLibrariesPath,
    string? InteropOutputPath,
    string? Cpp2IlPath,
    IReadOnlyList<DeploymentInput> DeploymentInputs,
    DeploymentPolicyOptions DeploymentPolicies,
    string? AndroidSdkRoot,
    bool Align,
    string? KeystorePath,
    string? KeystorePassword,
    string? KeyAlias,
    string? KeyPassword)
{
    public static PatchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var deploymentInputs = new List<DeploymentInput>();
        var deploymentPolicyRules = new List<string>();
        var deploymentOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--deployment"] = "",
            ["--mod"] = "Mods",
            ["--plugin"] = "Plugins",
            ["--user-lib"] = "UserLibs",
            ["--user-data"] = "UserData"
        };
        var supportedValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "--apk", "--release", "--output", "--libil2cpp", "--metadata",
            "--unity-version", "--unity-libs", "--interop-output", "--cpp2il",
            "--deployment", "--mod", "--plugin", "--user-lib", "--user-data",
            "--deployment-profile", "--deployment-policy",
            "--android-sdk", "--keystore", "--ks-pass", "--ks-alias", "--key-pass"
        };
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--align") { flags.Add(name); continue; }
            if (!supportedValues.Contains(name))
                throw new ArgumentException($"Unknown option '{name}'.");
            if (index + 1 == args.Length)
                throw new ArgumentException($"Missing value for option '{name}'.");
            var value = args[++index];
            if (deploymentOptions.TryGetValue(name, out var targetDirectory))
                deploymentInputs.Add(new(Path.GetFullPath(value), targetDirectory));
            else if (name == "--deployment-policy")
                deploymentPolicyRules.Add(value);
            else if (!values.TryAdd(name, value))
                throw new ArgumentException($"Option '{name}' was supplied more than once.");
        }

        string Required(string name) => values.TryGetValue(name, out var value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing required option '{name}'.");
        string? OptionalPath(string name) => values.TryGetValue(name, out var value) ? Path.GetFullPath(value) : null;
        string? Optional(string name) => values.GetValueOrDefault(name);
        var options = new PatchOptions(
            Required("--apk"), OptionalPath("--release"), Required("--output"),
            OptionalPath("--libil2cpp"), OptionalPath("--metadata"), Optional("--unity-version"),
            OptionalPath("--unity-libs"), OptionalPath("--interop-output"), OptionalPath("--cpp2il"), deploymentInputs,
            DeploymentPolicyOptions.Create(Optional("--deployment-profile"), deploymentPolicyRules),
            OptionalPath("--android-sdk"), flags.Contains("--align"), OptionalPath("--keystore"),
            Optional("--ks-pass"), Optional("--ks-alias"), Optional("--key-pass"));
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(options.ApkPath, options.OutputPath, pathComparison))
            throw new ArgumentException("The output APK must not overwrite the input APK.");
        if (options.KeystorePath is not null &&
            (string.IsNullOrWhiteSpace(options.KeystorePassword) ||
             string.IsNullOrWhiteSpace(options.KeyAlias)))
        {
            throw new ArgumentException("Signing requires --ks-pass and --ks-alias.");
        }
        if (options.KeystorePath is null &&
            (options.KeystorePassword is not null ||
             options.KeyAlias is not null ||
             options.KeyPassword is not null))
        {
            throw new ArgumentException("Signing passwords and aliases require --keystore.");
        }
        return options;
    }
}

public sealed record DeploymentInput(string SourcePath, string TargetDirectory);

public sealed class ApkPatchPipeline(PatchOptions options)
{
    private const string Il2CppEntry = "lib/arm64-v8a/libil2cpp.so";
    private const string MetadataEntry = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    private const string ManagersEntry = "assets/bin/Data/globalgamemanagers";
    private const string MainEntry = "lib/arm64-v8a/libmain.so";
    private const string PayloadEntry = AndroidPayloadContract.PayloadManifestPath;
    private const int AssetLayoutVersion = AndroidPayloadContract.FormatVersion;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        RequireFile(options.ApkPath, "APK");
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        var workRoot = Path.Combine(Path.GetTempPath(), $"lemonloader-patcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var releaseArchive = options.ReleasePath ?? await ReleaseResolver.ResolveLatestAsync(
                Path.Combine(Path.GetDirectoryName(options.OutputPath)!, ".tools"),
                cancellationToken);
            RequireFile(releaseArchive, "LemonLoader Release");
            var releaseRoot = Path.Combine(workRoot, "release");
            ZipFile.ExtractToDirectory(releaseArchive, releaseRoot);
            ReleaseValidator.Validate(releaseRoot);

            var inputRoot = Path.Combine(workRoot, "interop-input");
            Directory.CreateDirectory(inputRoot);
            await ExtractInteropInputsAsync(inputRoot, cancellationToken);
            var unityVersion = options.UnityVersion ?? DetectUnityVersion(Path.Combine(inputRoot, "globalgamemanagers"));
            if (string.IsNullOrWhiteSpace(unityVersion))
                throw new InvalidOperationException("Unity version was not supplied and could not be detected from globalgamemanagers.");

            var generatedInteropRoot = Path.Combine(workRoot, "interop");
            await GenerateInteropAsync(inputRoot, generatedInteropRoot, unityVersion, cancellationToken);
            if (options.InteropOutputPath is not null)
                PublishInterop(generatedInteropRoot, options.InteropOutputPath);

            var unsignedApk = Path.Combine(workRoot, "unsigned.apk");
            File.Copy(options.ApkPath, unsignedApk, true);
            MergeZip(
                unsignedApk,
                releaseRoot,
                generatedInteropRoot,
                options.DeploymentInputs,
                options.DeploymentPolicies);
            await FinalizeApkAsync(unsignedApk, workRoot, cancellationToken);
            Console.WriteLine($"Patched APK: {options.OutputPath}");
            Console.WriteLine($"SHA-256: {Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(options.OutputPath))).ToLowerInvariant()}");
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, true);
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine($"Warning: could not remove temporary directory '{workRoot}': {cleanupException.Message}");
            }
        }
    }

    private async Task ExtractInteropInputsAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        using var apk = ZipFile.OpenRead(options.ApkPath);
        if (apk.GetEntry(MainEntry) is null || apk.GetEntry("lib/arm64-v8a/libunity.so") is null)
            throw new InvalidOperationException("The APK does not use the standard ARM64 Unity libmain.so startup layout.");
        await CopyInputAsync(
            apk,
            options.LibIl2CppPath,
            Il2CppEntry,
            Path.Combine(outputRoot, "libil2cpp.so"),
            cancellationToken);
        await CopyInputAsync(
            apk,
            options.MetadataPath,
            MetadataEntry,
            Path.Combine(outputRoot, "global-metadata.dat"),
            cancellationToken);
        var managers = apk.GetEntry(ManagersEntry);
        if (managers is not null)
            await ExtractEntryAsync(
                managers,
                Path.Combine(outputRoot, "globalgamemanagers"),
                cancellationToken);
    }

    private static async Task CopyInputAsync(
        ZipArchive apk,
        string? explicitPath,
        string entryName,
        string destination,
        CancellationToken cancellationToken)
    {
        if (explicitPath is not null) { RequireFile(explicitPath, entryName); File.Copy(explicitPath, destination, true); return; }
        var entry = apk.GetEntry(entryName) ?? throw new InvalidOperationException($"APK entry '{entryName}' was not found. Supply it explicitly.");
        await ExtractEntryAsync(entry, destination, cancellationToken);
    }

    private async Task GenerateInteropAsync(
        string inputRoot,
        string outputRoot,
        string unityVersion,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var cpp2Il = options.Cpp2IlPath ?? await Cpp2IlResolver.ResolveAsync(
            Path.Combine(Path.GetDirectoryName(options.OutputPath)!, ".tools"),
            cancellationToken);
        var unityDependencies = options.UnityLibrariesPath is null
            ? await UnityDependenciesResolver.ResolveAsync(
                Path.Combine(Path.GetDirectoryName(options.OutputPath)!, ".tools", "UnityDependencies"),
                unityVersion,
                cancellationToken)
            : UnityDependenciesResolver.UseLocal(options.UnityLibrariesPath, unityVersion);
        var dummyRoot = Path.Combine(Path.GetDirectoryName(outputRoot)!, "cpp2il");
        Directory.CreateDirectory(dummyRoot);
        await ProcessRunner.RunAsync(cpp2Il, cancellationToken,
            "--game-path", inputRoot, "--force-binary-path", Path.Combine(inputRoot, "libil2cpp.so"),
            "--force-metadata-path", Path.Combine(inputRoot, "global-metadata.dat"), "--force-unity-version", unityVersion,
            "--output-as", "dummydll", "--output-to", dummyRoot, "--use-processor", "attributeanalyzer,attributeinjector");
        await ProcessRunner.RunAsync("dotnet", cancellationToken,
            "tool", "restore", "--tool-manifest", ToolManifest.Path,
            "--add-source", "https://nuget.bepinex.dev/v3/index.json");
        await ProcessRunner.RunAsync(
            "dotnet",
            cancellationToken,
            BuildInteropGeneratorArguments(
                Path.GetFullPath(ToolManifest.FindToolDll()),
                inputRoot,
                dummyRoot,
                outputRoot,
                unityDependencies.DirectoryPath));
        NormalizeInteropDirectory(outputRoot);
        InteropGenerationManifest.Write(
            outputRoot,
            inputRoot,
            unityVersion,
            unityDependencies,
            cpp2Il,
            Cpp2IlResolver.Version,
            ToolManifest.Version);
    }

    internal static string[] BuildInteropGeneratorArguments(
        string toolDll,
        string inputRoot,
        string dummyRoot,
        string outputRoot,
        string unityDependenciesRoot) =>
    [
        toolDll,
        "generate",
        "--input", dummyRoot,
        "--output", outputRoot,
        "--unity", unityDependenciesRoot,
        "--game-assembly", Path.Combine(inputRoot, "libil2cpp.so"),
        "--no-xref-cache",
        "--use-opt-out-prefixing"
    ];

    private static void PublishInterop(string source, string destination) =>
        DirectoryPublisher.Replace(source, destination);

    internal static void MergeZip(
        string apkPath,
        string releaseRoot,
        string interopRoot,
        IReadOnlyList<DeploymentInput> deploymentInputs,
        DeploymentPolicyOptions? deploymentPolicies = null)
    {
        var payload = ReadPayloadDescriptor(Path.Combine(
            releaseRoot,
            PayloadEntry.Replace('/', Path.DirectorySeparatorChar)));
        using var archive = ZipFile.Open(apkPath, ZipArchiveMode.Update);
        ValidateUniqueEntries(archive);
        ValidatePrivateNativeLibraries(archive, payload.PrivateNativeLibraries);
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName == "assets/lemonloader_asset_hash.txt" ||
                     entry.FullName.StartsWith("assets/dotnet/", StringComparison.Ordinal) ||
                     entry.FullName.StartsWith("assets/MelonLoader/", StringComparison.Ordinal) ||
                     entry.FullName.StartsWith("assets/LemonLoader/", StringComparison.Ordinal)).ToArray())
        {
            entry.Delete();
        }
        RemovePackagingMetadata(archive);
        AddTree(archive, Path.Combine(releaseRoot, "assets"), "assets");
        ValidateNoForbiddenRuntimeEntries(archive);
        AddNativeTree(archive, Path.Combine(releaseRoot, "lib"));
        RemovePackagingMetadata(archive);
        foreach (var dll in Directory.GetFiles(interopRoot, "*.dll"))
            AddFile(archive, dll, $"assets/LemonLoader/runtime/interop/{Path.GetFileName(dll)}");
        var interopManifest = Path.Combine(interopRoot, InteropGenerationManifest.FileName);
        RequireFile(interopManifest, "Interop generation manifest");
        AddFile(
            archive,
            interopManifest,
            $"assets/LemonLoader/runtime/interop/{InteropGenerationManifest.FileName}");
        foreach (var input in deploymentInputs)
            AddDeploymentInput(archive, input);
        ValidateDeploymentEntries(archive);
        RefreshPayloadDescriptor(
            archive,
            payload,
            deploymentPolicies ?? DeploymentPolicyOptions.Create(null, []));
        ValidateUniqueEntries(archive);
    }

    private static void RefreshPayloadDescriptor(
        ZipArchive archive,
        PayloadDescriptor descriptor,
        DeploymentPolicyOptions deploymentPolicies)
    {
        var deploymentFiles = BuildDeploymentFileDescriptors(archive, deploymentPolicies);
        deploymentPolicies.ValidateRuleCoverage(deploymentFiles.Select(file => file.Path));
        var updated = descriptor with
        {
            RuntimeSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime"),
            LoaderSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime/loader"),
            DotnetSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime/dotnet"),
            InteropSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime/interop"),
            DeploymentSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "deployment"),
            DeploymentProfile = deploymentPolicies.Profile.ToString().ToLowerInvariant(),
            DeploymentRevisionSha256 = ComputeDeploymentRevision(deploymentFiles),
            DeploymentFiles = deploymentFiles
        };
        archive.GetEntry(PayloadEntry)?.Delete();
        var manifestEntry = archive.CreateEntry(PayloadEntry, CompressionLevel.Optimal);
        using var output = manifestEntry.Open();
        JsonSerializer.Serialize(output, updated, PayloadJsonOptions);
    }

    internal static string ComputeDeploymentRevision(
        IReadOnlyList<DeploymentFileDescriptor> files)
    {
        var lines = new List<string>(files.Count + 1) { "deployment-revision=1" };
        lines.AddRange(files
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => $"{file.Path}|{file.Size}|{file.Sha256}|{file.Policy}"));
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<DeploymentFileDescriptor> BuildDeploymentFileDescriptors(
        ZipArchive archive,
        DeploymentPolicyOptions deploymentPolicies)
    {
        const string prefix = "assets/LemonLoader/deployment/";
        return archive.Entries
            .Where(entry =>
                !string.IsNullOrEmpty(entry.Name) &&
                entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Select(entry =>
            {
                long length = 0;
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var input = entry.Open();
                var buffer = new byte[64 * 1024];
                int bytesRead;
                while ((bytesRead = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    hasher.AppendData(buffer, 0, bytesRead);
                    length += bytesRead;
                }
                var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                var path = entry.FullName[prefix.Length..];
                return new DeploymentFileDescriptor(
                    path,
                    length,
                    hash,
                    DeploymentPolicyOptions.ToManifestValue(deploymentPolicies.Resolve(path)));
            })
            .ToArray();
    }

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed record PayloadDescriptor(
        int FormatVersion,
        string RuntimeSha256,
        string? LoaderSha256,
        string? DotnetSha256,
        string? InteropSha256,
        string DeploymentSha256,
        string DeploymentProfile,
        string DeploymentRevisionSha256,
        IReadOnlyList<DeploymentFileDescriptor> DeploymentFiles,
        IReadOnlyList<string> PrivateNativeLibraries);

    internal sealed record DeploymentFileDescriptor(
        string Path,
        long Size,
        string Sha256,
        string Policy);

    private static PayloadDescriptor ReadPayloadDescriptor(string path)
    {
        RequireFile(path, "Android payload manifest");
        var descriptor = JsonSerializer.Deserialize<PayloadDescriptor>(
            File.ReadAllText(path),
            PayloadJsonOptions) ?? throw new InvalidDataException("Android payload manifest is empty.");
        if (descriptor.FormatVersion != AssetLayoutVersion)
            throw new InvalidDataException(
                $"Unsupported Android payload layout {descriptor.FormatVersion}; expected {AssetLayoutVersion}.");
        if (descriptor.DeploymentFiles is null || descriptor.DeploymentProfile is null ||
            descriptor.DeploymentRevisionSha256 is null)
            throw new InvalidDataException(
                "Android payload manifest does not define deployment policy metadata.");
        if (descriptor.PrivateNativeLibraries.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                name.Contains('/') ||
                name.Contains('\\') ||
                !name.EndsWith(".so", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Android payload manifest contains an invalid private native library name.");
        }
        return descriptor;
    }

    private static void ValidateUniqueEntries(ZipArchive archive)
    {
        var duplicate = archive.Entries
            .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException($"APK contains duplicate ZIP entry '{duplicate.Key}'.");
    }

    private static void ValidateNoForbiddenRuntimeEntries(ZipArchive archive)
    {
        var forbidden = archive.Entries.FirstOrDefault(entry =>
            AndroidPayloadContract.IsForbiddenReleasePath(entry.FullName));
        if (forbidden is not null)
        {
            throw new InvalidDataException(
                $"Android payload must not contain loader documentation '{forbidden.FullName}'.");
        }
    }

    private static void ValidateDeploymentEntries(ZipArchive archive)
    {
        const string prefix = "assets/LemonLoader/deployment/";
        var paths = archive.Entries
            .Where(entry =>
                !string.IsNullOrEmpty(entry.Name) &&
                entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => entry.FullName[prefix.Length..])
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var files = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            try
            {
                DeploymentPolicyOptions.ValidateRelativePath(path, "deployment file");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }
            var separator = path.IndexOf('/');
            while (separator >= 0)
            {
                var parent = path[..separator];
                if (files.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"Deployment target '{parent}' conflicts with child file '{path}'.");
                }
                separator = path.IndexOf('/', separator + 1);
            }
        }
    }

    private static void ValidatePrivateNativeLibraries(
        ZipArchive archive,
        IReadOnlyList<string> privateNativeLibraries)
    {
        foreach (var library in privateNativeLibraries)
        {
            var publicEntry = $"lib/arm64-v8a/{library}";
            if (archive.GetEntry(publicEntry) is not null)
            {
                throw new InvalidDataException(
                    $"The original APK contains '{publicEntry}', which conflicts with LemonLoader's " +
                    "private .NET native dependency. This APK cannot be patched without isolating that dependency first.");
            }
        }
    }

    private static void RemovePackagingMetadata(ZipArchive archive)
    {
        foreach (var name in new[]
                 {
                     "lemonloader-release.json",
                     "assets/lemonloader-release.json",
                     "assets/lemon_patch_date.txt"
                 })
        {
            archive.GetEntry(name)?.Delete();
        }
    }

    private async Task FinalizeApkAsync(
        string unsignedApk,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var aligned = Path.Combine(workRoot, "aligned.apk");
        var signed = Path.Combine(workRoot, "signed.apk");
        var needsAlignment = options.Align || options.KeystorePath is not null;
        var sign = options.KeystorePath is not null;
        if (!needsAlignment)
        {
            DirectoryPublisher.ReplaceFile(unsignedApk, options.OutputPath);
            return;
        }
        var tools = AndroidBuildTools.Resolve(options.AndroidSdkRoot);
        await ProcessRunner.RunAsync(
            tools.ZipAlign,
            cancellationToken,
            "-P", "16", "-f", "4", unsignedApk, aligned);
        if (!sign)
        {
            DirectoryPublisher.ReplaceFile(aligned, options.OutputPath);
            return;
        }
        if (options.KeystorePassword is null || options.KeyAlias is null)
            throw new InvalidOperationException("Signing requires --ks-pass and --ks-alias.");
        await ProcessRunner.RunAsync(tools.ApkSigner, cancellationToken,
            "sign", "--ks", options.KeystorePath!, "--ks-key-alias", options.KeyAlias,
            "--ks-pass", $"pass:{options.KeystorePassword}", "--key-pass", $"pass:{options.KeyPassword ?? options.KeystorePassword}",
            "--v4-signing-enabled", "false", "--out", signed, aligned);
        await ProcessRunner.RunAsync(
            tools.ZipAlign,
            cancellationToken,
            "-P", "16", "-c", "4", signed);
        await ProcessRunner.RunAsync(
            tools.ApkSigner,
            cancellationToken,
            "verify", "--verbose", signed);
        DirectoryPublisher.ReplaceFile(signed, options.OutputPath);
    }

    private static string? DetectUnityVersion(string path)
    {
        if (!File.Exists(path)) return null;
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        return System.Text.RegularExpressions.Regex.Matches(text, @"(?<![0-9])\d+\.\d+\.\d+[abfp]\d+(?![0-9])")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).SingleOrDefault();
    }

    private static void AddTree(ZipArchive archive, string root, string prefix)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            AddFile(archive, file, $"{prefix}/{Path.GetRelativePath(root, file).Replace('\\', '/')}");
    }

    private static void AddNativeTree(ZipArchive archive, string root)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = $"lib/{Path.GetRelativePath(root, file).Replace('\\', '/')}";
            AddFile(archive, file, name, replaceExisting: name == MainEntry);
        }
    }

    private static void AddDeploymentInput(ZipArchive archive, DeploymentInput input)
    {
        var prefix = "assets/LemonLoader/deployment";
        if (!string.IsNullOrEmpty(input.TargetDirectory))
            prefix += $"/{input.TargetDirectory}";
        if (File.Exists(input.SourcePath))
        {
            if (string.IsNullOrEmpty(input.TargetDirectory))
                throw new InvalidDataException("A --deployment input must be a directory.");
            AddFile(archive, input.SourcePath, $"{prefix}/{Path.GetFileName(input.SourcePath)}");
            return;
        }
        if (!Directory.Exists(input.SourcePath))
            throw new FileNotFoundException(
                $"Deployment input was not found at '{input.SourcePath}'.",
                input.SourcePath);

        var files = Directory.GetFiles(input.SourcePath, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
            throw new InvalidDataException($"Deployment directory '{input.SourcePath}' contains no files.");
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(input.SourcePath, file).Replace('\\', '/');
            if (string.IsNullOrEmpty(input.TargetDirectory))
                ValidateDeploymentRootPath(relativePath);
            AddFile(archive, file, $"{prefix}/{relativePath}");
        }
    }

    private static void ValidateDeploymentRootPath(string relativePath)
    {
        var topLevel = relativePath.Split('/', 2)[0];
        foreach (var standardDirectory in new[] { "Mods", "Plugins", "UserLibs", "UserData" })
        {
            if (string.Equals(topLevel, standardDirectory, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(topLevel, standardDirectory, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Deployment directory '{topLevel}' must use Android casing '{standardDirectory}'.");
            }
        }
    }

    private static void AddFile(
        ZipArchive archive,
        string path,
        string name,
        bool replaceExisting = false)
    {
        var existing = archive.GetEntry(name);
        if (existing is not null && !replaceExisting)
            throw new InvalidDataException(
                $"Refusing to overwrite existing APK entry '{name}'. Only '{MainEntry}' may be replaced.");
        existing?.Delete();
        archive.CreateEntryFromFile(path, name,
            name.StartsWith("lib/", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void NormalizeInteropDirectory(string directoryPath)
    {
        var assemblyPaths = Directory.GetFiles(directoryPath, "*.dll");
        foreach (var path in assemblyPaths)
        {
            var temporaryPath = path + ".patched";
            var changed = false;
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(directoryPath);
                using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadingMode = ReadingMode.Immediate
                });
                foreach (var type in assembly.MainModule.Types.SelectMany(EnumerateTypes))
                foreach (var method in type.Methods)
                foreach (var parameter in method.Parameters)
                {
                    if (!parameter.HasDefault || parameter.HasConstant) continue;
                    parameter.Attributes &= ~Mono.Cecil.ParameterAttributes.HasDefault;
                    changed = true;
                }
                if (changed) assembly.Write(temporaryPath);
            }
            if (changed) File.Move(temporaryPath, path, true);
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(EnumerateTypes)) yield return nested;
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{description} was not found at '{path}'.");
    }
}

internal static class ProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static async Task RunAsync(
        string fileName,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Console.WriteLine($"Running {Path.GetFileName(fileName)}...");
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"'{fileName}' did not finish within {DefaultTimeout.TotalMinutes:0} minutes.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"'{fileName}' failed with exit code {process.ExitCode}.");
    }
}

internal static class ToolManifest
{
    public const string Version = "1.5.1-ci.845";
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, ".config", "dotnet-tools.json");
    public static string FindToolDll()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var store = System.IO.Path.Combine(home, ".nuget", "packages", "il2cppinterop.cli", Version);
        return Directory.GetFiles(store, "Il2CppInterop.CLI.dll", SearchOption.AllDirectories).Single();
    }
}

internal static class Cpp2IlResolver
{
    public const string Version = "2022.1.0-pre-release.21";
    private static readonly Dictionary<string, string> Hashes = new(StringComparer.Ordinal)
    {
        ["Windows.exe"] = "663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c",
        ["Linux"] = "526998e593c52c029c5a6215c5c6c9f9d963706bfc409fc9ff80a95c4c500349",
        ["Linux-ARM64"] = "2d8e76b11f52fd85440ec60abf87d5c3455ea54f5104e0af51da15ba4440871b",
        ["OSX"] = "15faab020698512807f792aef32a89fe529d41d2cc03955dd3516f0519fe8f72",
        ["OSX-ARM64"] = "6670ddb93f28d4e7f329251d2597d2abbc6ba23e46382f7ca7b5e437ec93606a"
    };

    public static async Task<string> ResolveAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var suffix = OperatingSystem.IsWindows() ? "Windows.exe" : OperatingSystem.IsLinux()
            ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "Linux-ARM64" : "Linux")
            : (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "OSX-ARM64" : "OSX");
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, $"Cpp2IL-{Version}-{suffix}");
        if (!File.Exists(path))
        {
            using var client = new HttpClient();
            var content = await client.GetByteArrayAsync(
                $"https://github.com/SamboyCoding/Cpp2IL/releases/download/{Version}/Cpp2IL-{Version}-{suffix}",
                cancellationToken);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        if (!string.Equals(hash, Hashes[suffix], StringComparison.Ordinal)) throw new InvalidOperationException("Cpp2IL SHA-256 mismatch.");
        return path;
    }
}

internal static class ReleaseResolver
{
    private const string ReleaseFileName = "LemonLoader-Android-arm64.zip";
    private const string LatestUrl = "https://github.com/LemonLoader/MelonLoader/releases/latest/download/LemonLoader-Android-arm64.zip";

    public static async Task<string> ResolveLatestAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var bundledCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ReleaseFileName),
            Path.Combine(AppContext.BaseDirectory, "..", ReleaseFileName),
            Path.Combine(Environment.CurrentDirectory, ReleaseFileName)
        };
        foreach (var candidate in bundledCandidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                Console.WriteLine($"Using bundled LemonLoader Release: {candidate}");
                return candidate;
            }
        }

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ReleaseFileName);
        if (File.Exists(path))
        {
            Console.WriteLine($"Using cached LemonLoader Release: {path}");
            return path;
        }

        using var client = new HttpClient();
        try
        {
            using var response = await client.GetAsync(LatestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(path);
            await response.Content.CopyToAsync(output, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            var status = exception.StatusCode is null
                ? "no HTTP status"
                : $"{(int)exception.StatusCode.Value} {exception.StatusCode.Value}";
            throw new InvalidOperationException(
                $"Could not download the latest LemonLoader Release from '{LatestUrl}' " +
                $"({status}). " +
                "Supply --release <LemonLoader-Android-arm64.zip> or publish the Patcher with a bundled Release.",
                exception);
        }
        return path;
    }
}

internal sealed record AndroidBuildTools(string ZipAlign, string ApkSigner)
{
    public static AndroidBuildTools Resolve(string? sdkRoot)
    {
        sdkRoot ??= Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (sdkRoot is null) throw new InvalidOperationException("Set ANDROID_SDK_ROOT or pass --android-sdk.");
        var buildTools = Directory.GetDirectories(System.IO.Path.Combine(sdkRoot, "build-tools"))
            .OrderByDescending(path => Version.TryParse(System.IO.Path.GetFileName(path), out var version) ? version : new Version()).First();
        var zipAlign = System.IO.Path.Combine(buildTools, OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign");
        var signer = System.IO.Path.Combine(buildTools, OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner");
        return new(zipAlign, signer);
    }
}
