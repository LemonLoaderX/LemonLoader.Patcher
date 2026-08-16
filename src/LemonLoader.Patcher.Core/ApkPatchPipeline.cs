using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;

public sealed class ApkPatchPipeline
{
    private readonly PatchRequest request;
    private readonly IProgress<PatcherMessage>? progress;

    public ApkPatchPipeline(
        PatchRequest request,
        IProgress<PatcherMessage>? progress = null)
    {
        this.request = request.NormalizeAndValidate();
        this.progress = progress;
    }
    private const string Il2CppEntry = "lib/arm64-v8a/libil2cpp.so";
    private const string MetadataEntry = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    private const string ManagersEntry = "assets/bin/Data/globalgamemanagers";
    private const string MainEntry = "lib/arm64-v8a/libmain.so";
    private const string PayloadEntry = AndroidPayloadContract.PayloadManifestPath;
    private const int AssetLayoutVersion = AndroidPayloadContract.FormatVersion;

    public async Task<PatchResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ReportStage("Validating inputs");
        RequireFile(request.InputApkPath, "APK");
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputApkPath)!);
        var workRoot = Path.Combine(Path.GetTempPath(), $"lemonloader-patcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            ReportStage("Resolving LemonLoader Release");
            var releaseArchive = request.ReleasePath ?? await ReleaseResolver.ResolveLatestAsync(
                Path.Combine(Path.GetDirectoryName(request.OutputApkPath)!, ".tools"),
                progress,
                cancellationToken);
            RequireFile(releaseArchive, "LemonLoader Release");
            var releaseRoot = Path.Combine(workRoot, "release");
            ZipFile.ExtractToDirectory(releaseArchive, releaseRoot);
            ReleaseValidator.Validate(releaseRoot);

            ReportStage("Reading Unity game data");
            var inputRoot = Path.Combine(workRoot, "interop-input");
            Directory.CreateDirectory(inputRoot);
            await ExtractInteropInputsAsync(inputRoot, cancellationToken);
            var unityVersion = request.UnityVersion ?? DetectUnityVersion(Path.Combine(inputRoot, "globalgamemanagers"));
            if (string.IsNullOrWhiteSpace(unityVersion))
                throw new InvalidOperationException("Unity version was not supplied and could not be detected from globalgamemanagers.");

            ReportStage($"Generating Interop assemblies for Unity {unityVersion}");
            var generatedInteropRoot = Path.Combine(workRoot, "interop");
            await GenerateInteropAsync(inputRoot, generatedInteropRoot, unityVersion, cancellationToken);
            if (request.InteropOutputPath is not null)
                PublishInterop(generatedInteropRoot, request.InteropOutputPath);

            ReportStage("Packaging APK payload");
            var unsignedApk = Path.Combine(workRoot, "unsigned.apk");
            File.Copy(request.InputApkPath, unsignedApk, true);
            MergeZip(
                unsignedApk,
                releaseRoot,
                generatedInteropRoot,
                request.DeploymentPath,
                request.DeploymentPolicies);
            await FinalizeApkAsync(unsignedApk, workRoot, cancellationToken);
            await using var outputStream = File.OpenRead(request.OutputApkPath);
            var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(outputStream, cancellationToken))
                .ToLowerInvariant();
            ReportStage("APK ready");
            return new(request.OutputApkPath, hash, unityVersion);
        }
        finally
        {
            try
            {
                Directory.Delete(workRoot, true);
            }
            catch (Exception cleanupException)
            {
                progress?.Report(new(
                    PatcherMessageKind.Warning,
                    $"Could not remove temporary directory '{workRoot}': {cleanupException.Message}"));
            }
        }
    }

    private void ReportStage(string message) =>
        progress?.Report(new(PatcherMessageKind.Stage, message));

    private async Task ExtractInteropInputsAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        using var apk = ZipFile.OpenRead(request.InputApkPath);
        if (apk.GetEntry(MainEntry) is null || apk.GetEntry("lib/arm64-v8a/libunity.so") is null)
            throw new InvalidOperationException("The APK does not use the standard ARM64 Unity libmain.so startup layout.");
        await CopyInputAsync(
            apk,
            request.GameAssemblyPath,
            Il2CppEntry,
            Path.Combine(outputRoot, "libil2cpp.so"),
            cancellationToken);
        await CopyInputAsync(
            apk,
            request.MetadataPath,
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
        var cpp2Il = request.Cpp2IlPath ?? await Cpp2IlResolver.ResolveAsync(
            Path.Combine(Path.GetDirectoryName(request.OutputApkPath)!, ".tools"),
            progress,
            cancellationToken);
        var unityDependencies = request.UnityLibrariesPath is null
            ? await UnityDependenciesResolver.ResolveAsync(
                Path.Combine(Path.GetDirectoryName(request.OutputApkPath)!, ".tools", "UnityDependencies"),
                unityVersion,
                progress,
                cancellationToken)
            : UnityDependenciesResolver.UseLocal(request.UnityLibrariesPath, unityVersion);
        var dummyRoot = Path.Combine(Path.GetDirectoryName(outputRoot)!, "cpp2il");
        Directory.CreateDirectory(dummyRoot);
        await ProcessRunner.RunAsync(cpp2Il, progress, cancellationToken,
            "--game-path", inputRoot, "--force-binary-path", Path.Combine(inputRoot, "libil2cpp.so"),
            "--force-metadata-path", Path.Combine(inputRoot, "global-metadata.dat"), "--force-unity-version", unityVersion,
            "--output-as", "dummydll", "--output-to", dummyRoot, "--use-processor", "attributeanalyzer,attributeinjector");
        await ProcessRunner.RunAsync("dotnet", progress, cancellationToken,
            "tool", "restore", "--tool-manifest", ToolManifest.Path,
            "--add-source", "https://nuget.bepinex.dev/v3/index.json");
        await ProcessRunner.RunAsync(
            "dotnet",
            progress,
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
        string? deploymentPath,
        DeploymentPolicyOptions? deploymentPolicies = null)
    {
        var payload = ReadPayloadDescriptor(Path.Combine(
            releaseRoot,
            PayloadEntry.Replace('/', Path.DirectorySeparatorChar)));
        using var archive = ZipFile.Open(apkPath, ZipArchiveMode.Update);
        ValidateUniqueEntries(archive);
        ValidatePrivateNativeLibraries(archive, payload.PrivateNativeLibraries);
        RejectExistingLoaderPayload(archive);
        AddTree(archive, Path.Combine(releaseRoot, "assets"), "assets");
        ValidateNoForbiddenRuntimeEntries(archive);
        AddNativeTree(archive, Path.Combine(releaseRoot, "lib"));
        foreach (var dll in Directory.GetFiles(interopRoot, "*.dll"))
            AddFile(archive, dll, $"assets/LemonLoader/runtime/interop/{Path.GetFileName(dll)}");
        var interopManifest = Path.Combine(interopRoot, InteropGenerationManifest.FileName);
        RequireFile(interopManifest, "Interop generation manifest");
        AddFile(
            archive,
            interopManifest,
            $"assets/LemonLoader/runtime/interop/{InteropGenerationManifest.FileName}");
        if (deploymentPath is not null)
            AddDeploymentRoot(archive, deploymentPath);
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

    private static void RejectExistingLoaderPayload(ZipArchive archive)
    {
        var existing = archive.Entries.FirstOrDefault(entry =>
            entry.FullName == "assets/lemonloader_asset_hash.txt" ||
            entry.FullName.StartsWith("assets/dotnet/", StringComparison.Ordinal) ||
            entry.FullName.StartsWith("assets/MelonLoader/", StringComparison.Ordinal) ||
            entry.FullName.StartsWith("assets/LemonLoader/", StringComparison.Ordinal));
        if (existing is not null)
        {
            throw new InvalidDataException(
                $"The input APK already contains a loader payload at '{existing.FullName}'. " +
                "Patch an original game APK instead.");
        }
    }

    private async Task FinalizeApkAsync(
        string unsignedApk,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var aligned = Path.Combine(workRoot, "aligned.apk");
        var signed = Path.Combine(workRoot, "signed.apk");
        var sign = request.Signing is not null;
        var tools = AndroidBuildTools.Resolve(request.AndroidSdkRoot);
        ReportStage("Aligning APK for 16 KiB pages");
        await ProcessRunner.RunAsync(
            tools.ZipAlign,
            progress,
            cancellationToken,
            "-P", "16", "-f", "4", unsignedApk, aligned);
        if (!sign)
        {
            DirectoryPublisher.ReplaceFile(aligned, request.OutputApkPath);
            return;
        }
        var signing = request.Signing!;
        ReportStage("Signing and verifying APK");
        await ProcessRunner.RunAsync(tools.ApkSigner, progress, cancellationToken,
            "sign", "--ks", signing.KeystorePath, "--ks-key-alias", signing.KeyAlias,
            "--ks-pass", $"pass:{signing.StorePassword}", "--key-pass", $"pass:{signing.KeyPassword ?? signing.StorePassword}",
            "--v4-signing-enabled", "false", "--out", signed, aligned);
        await ProcessRunner.RunAsync(
            tools.ZipAlign,
            progress,
            cancellationToken,
            "-P", "16", "-c", "4", signed);
        await ProcessRunner.RunAsync(
            tools.ApkSigner,
            progress,
            cancellationToken,
            "verify", "--verbose", signed);
        DirectoryPublisher.ReplaceFile(signed, request.OutputApkPath);
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

    private static void AddDeploymentRoot(ZipArchive archive, string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Deployment directory was not found at '{sourcePath}'.",
                sourcePath);

        var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
            throw new InvalidDataException($"Deployment directory '{sourcePath}' contains no files.");
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourcePath, file).Replace('\\', '/');
            ValidateDeploymentRootPath(relativePath);
            AddFile(archive, file, $"assets/LemonLoader/deployment/{relativePath}");
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
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        progress?.Report(new(
            PatcherMessageKind.Stage,
            $"Running {Path.GetFileName(fileName)}"));
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var standardOutput = ForwardOutputAsync(process.StandardOutput, progress);
        var standardError = ForwardOutputAsync(process.StandardError, progress);
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
            await Task.WhenAll(standardOutput, standardError);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"'{fileName}' did not finish within {DefaultTimeout.TotalMinutes:0} minutes.");
        }
        await Task.WhenAll(standardOutput, standardError);
        if (process.ExitCode != 0) throw new InvalidOperationException($"'{fileName}' failed with exit code {process.ExitCode}.");
    }

    private static async Task ForwardOutputAsync(
        StreamReader reader,
        IProgress<PatcherMessage>? progress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                progress?.Report(new(PatcherMessageKind.ToolOutput, line));
        }
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
        IProgress<PatcherMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var suffix = OperatingSystem.IsWindows() ? "Windows.exe" : OperatingSystem.IsLinux()
            ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "Linux-ARM64" : "Linux")
            : (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "OSX-ARM64" : "OSX");
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, $"Cpp2IL-{Version}-{suffix}");
        if (File.Exists(path))
        {
            var cachedHash = await ComputeHashAsync(path, cancellationToken);
            if (string.Equals(cachedHash, Hashes[suffix], StringComparison.Ordinal))
                return path;
            File.Delete(path);
        }

        var temporaryPath = System.IO.Path.Combine(
            root,
            $".Cpp2IL-{Guid.NewGuid():N}.download");
        progress?.Report(new(PatcherMessageKind.Stage, $"Downloading Cpp2IL {Version}"));
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(
                $"https://github.com/SamboyCoding/Cpp2IL/releases/download/{Version}/Cpp2IL-{Version}-{suffix}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(output, cancellationToken);
            }
            var hash = await ComputeHashAsync(temporaryPath, cancellationToken);
            if (!string.Equals(hash, Hashes[suffix], StringComparison.Ordinal))
                throw new InvalidDataException("Downloaded Cpp2IL SHA-256 does not match the pinned tool.");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.Move(temporaryPath, path, true);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        static async Task<string> ComputeHashAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            await using var input = File.OpenRead(filePath);
            return Convert.ToHexString(
                    await SHA256.HashDataAsync(input, cancellationToken))
                .ToLowerInvariant();
        }
    }
}

internal static class ReleaseResolver
{
    private const string ReleaseFileName = "LemonLoader-Android-arm64.zip";
    private const string LatestUrl = "https://github.com/LemonLoader/MelonLoader/releases/latest/download/LemonLoader-Android-arm64.zip";

    public static async Task<string> ResolveLatestAsync(
        string root,
        IProgress<PatcherMessage>? progress = null,
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
                progress?.Report(new(
                    PatcherMessageKind.Stage,
                    $"Using bundled LemonLoader Release: {candidate}"));
                return candidate;
            }
        }

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ReleaseFileName);
        if (File.Exists(path) && IsReadableRelease(path))
        {
            progress?.Report(new(
                PatcherMessageKind.Stage,
                $"Using cached LemonLoader Release: {path}"));
            return path;
        }
        if (File.Exists(path))
            File.Delete(path);

        progress?.Report(new(PatcherMessageKind.Stage, "Downloading LemonLoader Release"));
        var temporaryPath = Path.Combine(root, $".{ReleaseFileName}.{Guid.NewGuid():N}.download");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(
                LatestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(output, cancellationToken);
            }
            if (!IsReadableRelease(temporaryPath))
                throw new InvalidDataException("Downloaded LemonLoader Release is not a valid Release archive.");
            File.Move(temporaryPath, path, true);
            return path;
        }
        catch (HttpRequestException exception)
        {
            var status = exception.StatusCode is null
                ? "no HTTP status"
                : $"{(int)exception.StatusCode.Value} {exception.StatusCode.Value}";
            throw new InvalidOperationException(
                $"Could not download the latest LemonLoader Release from '{LatestUrl}' " +
                $"({status}). " +
                "Choose a local Release archive or publish the Patcher with a bundled Release.",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        static bool IsReadableRelease(string archivePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                return archive.GetEntry("lemonloader-release.json") is not null &&
                       archive.GetEntry(AndroidPayloadContract.PayloadManifestPath) is not null;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }
}

internal sealed record AndroidBuildTools(string ZipAlign, string ApkSigner)
{
    public static AndroidBuildTools Resolve(string? sdkRoot)
    {
        sdkRoot ??= Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (sdkRoot is null)
        {
            throw new InvalidOperationException(
                "Android SDK was not found. Set ANDROID_SDK_ROOT or choose an SDK directory.");
        }
        sdkRoot = System.IO.Path.GetFullPath(sdkRoot);
        var buildToolsRoot = System.IO.Path.Combine(sdkRoot, "build-tools");
        if (!Directory.Exists(buildToolsRoot))
        {
            throw new InvalidOperationException(
                $"Android SDK build-tools were not found under '{sdkRoot}'.");
        }
        var buildTools = Directory.GetDirectories(buildToolsRoot)
            .Select(path => new
            {
                Path = path,
                Version = Version.TryParse(System.IO.Path.GetFileName(path), out var version)
                    ? version
                    : null
            })
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .Select(item => item.Path)
            .FirstOrDefault() ?? throw new InvalidOperationException(
                $"Android SDK '{sdkRoot}' does not contain a versioned build-tools directory.");
        var zipAlign = System.IO.Path.Combine(buildTools, OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign");
        var signer = System.IO.Path.Combine(buildTools, OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner");
        RequireTool(zipAlign, "zipalign");
        RequireTool(signer, "apksigner");
        return new(zipAlign, signer);

        static void RequireTool(string path, string name)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"Android build tool '{name}' was not found at '{path}'.");
        }
    }
}
