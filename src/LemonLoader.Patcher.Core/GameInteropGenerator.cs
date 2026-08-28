using System.IO.Compression;
using System.Text;
using Mono.Cecil;

internal sealed record GeneratedInterop(string DirectoryPath, string UnityVersion);

internal sealed class GameInteropGenerator(
    PatchRequest request,
    IProgress<PatcherMessage>? progress)
{
    public async Task<GeneratedInterop> GenerateAsync(
        string workRoot,
        CancellationToken cancellationToken)
    {
        ReportStage("Reading Unity game data");
        var inputRoot = Path.Combine(workRoot, "interop-input");
        Directory.CreateDirectory(inputRoot);
        await ExtractInputsAsync(inputRoot, cancellationToken);
        var unityVersion = request.UnityVersion ??
                           DetectUnityVersion(Path.Combine(inputRoot, "globalgamemanagers"));
        if (string.IsNullOrWhiteSpace(unityVersion))
        {
            throw new InvalidOperationException(
                "Unity version was not supplied and could not be detected from globalgamemanagers.");
        }

        ReportStage($"Generating Interop assemblies for Unity {unityVersion}");
        var outputRoot = Path.Combine(workRoot, "interop");
        await GenerateAssembliesAsync(inputRoot, outputRoot, unityVersion, cancellationToken);
        if (request.InteropOutputPath is not null)
            DirectoryPublisher.Replace(outputRoot, request.InteropOutputPath);
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

    private async Task ExtractInputsAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        if (request.InputKind == PatchInputKind.Directory)
        {
            ValidateUnityLayout(
                File.Exists(GamePackageLayout.FilePath(
                    request.InputPath,
                    GamePackageLayout.MainLibrary)),
                File.Exists(GamePackageLayout.FilePath(
                    request.InputPath,
                    GamePackageLayout.UnityLibrary)),
                "input directory");
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
            var managersPath = GamePackageLayout.FilePath(
                request.InputPath,
                GamePackageLayout.GlobalGameManagers);
            if (File.Exists(managersPath))
                File.Copy(managersPath, Path.Combine(outputRoot, "globalgamemanagers"), true);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        using var apk = ZipFile.OpenRead(request.InputPath);
        ValidateUnityLayout(
            apk.GetEntry(GamePackageLayout.MainLibrary) is not null,
            apk.GetEntry(GamePackageLayout.UnityLibrary) is not null,
            "APK");
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
        if (apk.GetEntry(GamePackageLayout.GlobalGameManagers) is { } managers)
        {
            await ExtractEntryAsync(
                managers,
                Path.Combine(outputRoot, "globalgamemanagers"),
                cancellationToken);
        }
    }

    private async Task GenerateAssembliesAsync(
        string inputRoot,
        string outputRoot,
        string unityVersion,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var interopTool = request.Il2CppInteropCliPath is null
            ? InteropGeneratorTool.FromBundledFork(BundledInteropGeneratorTool.FindToolDll())
            : InteropGeneratorTool.FromOverride(request.Il2CppInteropCliPath);
        var cpp2Il = request.Cpp2IlPath ?? await Cpp2IlResolver.ResolveAsync(
            request.ToolCacheRoot,
            progress,
            cancellationToken);
        var unityDependencies = request.UnityLibrariesPath is null
            ? await UnityDependenciesResolver.ResolveAsync(
                Path.Combine(request.ToolCacheRoot, "UnityDependencies"),
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
        NormalizeAssemblies(outputRoot);
        InteropGenerationManifest.Write(
            outputRoot,
            inputRoot,
            unityVersion,
            unityDependencies,
            cpp2Il,
            Cpp2IlResolver.Version,
            interopTool);
    }

    private static void ValidateUnityLayout(bool hasMain, bool hasUnity, string description)
    {
        if (!hasMain || !hasUnity)
        {
            throw new InvalidOperationException(
                $"The {description} does not use the standard ARM64 Unity libmain.so startup layout.");
        }
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

    private static string? DetectUnityVersion(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        return System.Text.RegularExpressions.Regex.Matches(
                text,
                @"(?<![0-9])\d+\.\d+\.\d+[abfp]\d+(?![0-9])")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .SingleOrDefault();
    }

    private static void NormalizeAssemblies(string directoryPath)
    {
        foreach (var path in Directory.GetFiles(directoryPath, "*.dll"))
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
                            if (!parameter.HasDefault || parameter.HasConstant)
                                continue;
                            parameter.Attributes &= ~Mono.Cecil.ParameterAttributes.HasDefault;
                            changed = true;
                        }
                if (changed)
                    assembly.Write(temporaryPath);
            }
            if (changed)
                File.Move(temporaryPath, path, true);
        }
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(EnumerateTypes))
            yield return nested;
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{description} was not found at '{path}'.");
    }

    private void ReportStage(string message) =>
        progress?.Report(new(PatcherMessageKind.Stage, message));
}
