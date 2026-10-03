using System.IO.Compression;

internal sealed record GeneratedInterop(string DirectoryPath, string UnityVersion);

internal sealed class GameInteropGenerator(
    InteropRequest request,
    IProgress<PatcherMessage>? progress)
{
    public async Task<GeneratedInterop> GenerateAsync(
        string workRoot,
        CancellationToken cancellationToken)
    {
        ReportStage("Reading Unity game data");
        var inputRoot = Path.Combine(workRoot, "interop-input");
        Directory.CreateDirectory(inputRoot);
        var unityVersion = await ExtractInputsAsync(inputRoot, cancellationToken);

        ReportStage($"Generating Interop assemblies for Unity {unityVersion}");
        var outputRoot = Path.Combine(workRoot, "interop");
        await GenerateAssembliesAsync(inputRoot, outputRoot, unityVersion, cancellationToken);
        if (request.OutputPath is not null)
            DirectoryPublisher.Replace(outputRoot, request.OutputPath, progress, cancellationToken);
        return new(outputRoot, unityVersion);
    }

    internal static string[] BuildGeneratorArguments(
        string toolDll,
        string inputRoot,
        string dummyRoot,
        string outputRoot,
        string unityDependenciesRoot) =>
    [
        "--roll-forward", "Major",
        toolDll,
        "generate",
        "--input", dummyRoot,
        "--output", outputRoot,
        "--unity", unityDependenciesRoot,
        "--game-assembly", Path.Combine(inputRoot, "libil2cpp.so"),
        "--no-xref-cache",
        "--use-opt-out-prefixing"
    ];

    internal async Task<string> ExtractInputsAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        if (request.InputPath is null)
        {
            DirectoryPublisher.CopyFile(request.GameAssemblyPath!, Path.Combine(outputRoot, "libil2cpp.so"), cancellationToken);
            DirectoryPublisher.CopyFile(request.MetadataPath!, Path.Combine(outputRoot, "global-metadata.dat"), cancellationToken);
            return request.UnityVersion!;
        }
        if (request.InputKind == PatchInputKind.Directory)
        {
            var unityVersion = UnityVersionDetector.FromDirectory(
                request.InputPath, request.UnityVersion, cancellationToken);
            CopyDirectoryInput(
                request.GameAssemblyPath,
                GamePackageLayout.FilePath(request.InputPath, GamePackageLayout.Il2CppLibrary),
                Path.Combine(outputRoot, "libil2cpp.so"),
                GamePackageLayout.Il2CppLibrary);
            CopyDirectoryInput(
                request.MetadataPath,
                GamePackageLayout.FilePath(request.InputPath, GamePackageLayout.Metadata),
                Path.Combine(outputRoot, "global-metadata.dat"),
                GamePackageLayout.Metadata);
            cancellationToken.ThrowIfCancellationRequested();
            return unityVersion;
        }

        using var apk = ZipFile.OpenRead(request.InputPath);
        ArchiveSafety.Validate(apk);
        var apkUnityVersion = UnityVersionDetector.FromApk(apk, request.UnityVersion, cancellationToken);
        await CopyApkInputAsync(
            apk,
            request.GameAssemblyPath,
            GamePackageLayout.Il2CppLibrary,
            Path.Combine(outputRoot, "libil2cpp.so"),
            cancellationToken);
        await CopyApkInputAsync(
            apk,
            request.MetadataPath,
            GamePackageLayout.Metadata,
            Path.Combine(outputRoot, "global-metadata.dat"),
            cancellationToken);
        return apkUnityVersion;
    }

    private async Task GenerateAssembliesAsync(
        string inputRoot,
        string outputRoot,
        string unityVersion,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var recordIdentity = request.OutputPath is not null;
        var interopTool = request.Il2CppInteropCliPath is null
            ? InteropGeneratorTool.FromBundledFork(BundledInteropGeneratorTool.FindToolDll(), recordIdentity)
            : InteropGeneratorTool.FromOverride(request.Il2CppInteropCliPath, recordIdentity);
        var cpp2Il = request.Cpp2IlPath ?? await Cpp2IlResolver.ResolveAsync(
            ToolCachePaths.Root,
            progress,
            cancellationToken);
        var unityDependencies = request.UnityLibrariesPath is null
            ? await UnityDependenciesResolver.ResolveAsync(
                ToolCachePaths.UnityDependencies,
                unityVersion,
                progress,
                cancellationToken)
            : UnityDependenciesResolver.UseLocal(request.UnityLibrariesPath, unityVersion);
        var dummyRoot = Path.Combine(Path.GetDirectoryName(outputRoot)!, "cpp2il");
        Directory.CreateDirectory(dummyRoot);
        await ProcessRunner.RunAsync(
            cpp2Il,
            progress,
            cancellationToken,
            "--game-path", inputRoot,
            "--force-binary-path", Path.Combine(inputRoot, "libil2cpp.so"),
            "--force-metadata-path", Path.Combine(inputRoot, "global-metadata.dat"),
            "--force-unity-version", unityVersion,
            "--output-as", "dummydll",
            "--output-to", dummyRoot,
            "--use-processor", "attributeanalyzer,attributeinjector");
        ReportStage($"Using Il2CppInterop {interopTool.Version} from {interopTool.Source}");
        await ProcessRunner.RunAsync(
            "dotnet",
            progress,
            cancellationToken,
            BuildGeneratorArguments(
                interopTool.Path,
                inputRoot,
                dummyRoot,
                outputRoot,
                unityDependencies.DirectoryPath));
        _ = InteropInput.Assemblies(outputRoot);
        if (recordIdentity)
            InteropGenerationManifest.Write(
                outputRoot,
                inputRoot,
                unityVersion,
                unityDependencies,
                cpp2Il,
                Cpp2IlResolver.Version,
                interopTool);
    }

    private static void CopyDirectoryInput(
        string? explicitPath,
        string defaultPath,
        string destination,
        string entryName)
    {
        var source = explicitPath ?? defaultPath;
        RequireFile(source, entryName);
        File.Copy(source, destination, true);
    }

    private static async Task CopyApkInputAsync(
        ZipArchive apk,
        string? explicitPath,
        string entryName,
        string destination,
        CancellationToken cancellationToken)
    {
        if (explicitPath is not null)
        {
            RequireFile(explicitPath, entryName);
            File.Copy(explicitPath, destination, true);
            return;
        }
        var entry = apk.GetEntry(entryName)
            ?? throw new InvalidOperationException(
                $"APK entry '{entryName}' was not found. Supply it explicitly.");
        await ExtractEntryAsync(entry, destination, cancellationToken);
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

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{description} was not found at '{path}'.");
    }

    private void ReportStage(string message) =>
        progress?.Report(new(PatcherMessageKind.Stage, message));
}
