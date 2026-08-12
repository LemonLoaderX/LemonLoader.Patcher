using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;

return await PatcherApplication.RunAsync(args);

public static class PatcherApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] != "patch")
        {
            Console.Error.WriteLine("Unknown command. Use 'patch --help'.");
            return 2;
        }

        try
        {
            var options = PatchOptions.Parse(args[1..]);
            await new ApkPatchPipeline(options).RunAsync();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        LemonLoader.Patcher

        patch --apk <original.apk> [--release <LemonLoader-Android.zip>] --output <mod.apk>
              [--libil2cpp <libil2cpp.so> --metadata <global-metadata.dat>]
              [--unity-version <version>] [--interop-output <directory>]
              [--cpp2il <path>] [--mod <mod.dll>]...
              [--android-sdk <path>] [--align]
              [--keystore <path> --ks-pass <password> --ks-alias <alias> [--key-pass <password>]]

        The APK is read and updated as a ZIP. apktool is not required.
        """);
}

public sealed record PatchOptions(
    string ApkPath,
    string? ReleasePath,
    string OutputPath,
    string? LibIl2CppPath,
    string? MetadataPath,
    string? UnityVersion,
    string? InteropOutputPath,
    string? Cpp2IlPath,
    IReadOnlyList<string> ModPaths,
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
        var mods = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--align") { flags.Add(name); continue; }
            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 == args.Length)
                throw new ArgumentException($"Invalid option '{name}'.");
            var value = args[++index];
            if (name == "--mod") mods.Add(Path.GetFullPath(value));
            else values[name] = value;
        }

        string Required(string name) => values.TryGetValue(name, out var value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing required option '{name}'.");
        string? OptionalPath(string name) => values.TryGetValue(name, out var value) ? Path.GetFullPath(value) : null;
        string? Optional(string name) => values.GetValueOrDefault(name);
        return new(
            Required("--apk"), OptionalPath("--release"), Required("--output"),
            OptionalPath("--libil2cpp"), OptionalPath("--metadata"), Optional("--unity-version"),
            OptionalPath("--interop-output"), OptionalPath("--cpp2il"), mods,
            OptionalPath("--android-sdk"), flags.Contains("--align"), OptionalPath("--keystore"),
            Optional("--ks-pass"), Optional("--ks-alias"), Optional("--key-pass"));
    }
}

public sealed class ApkPatchPipeline(PatchOptions options)
{
    private const string Il2CppEntry = "lib/arm64-v8a/libil2cpp.so";
    private const string MetadataEntry = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    private const string ManagersEntry = "assets/bin/Data/globalgamemanagers";
    private const string MainEntry = "lib/arm64-v8a/libmain.so";

    public async Task RunAsync()
    {
        RequireFile(options.ApkPath, "APK");
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        var workRoot = Path.Combine(Path.GetTempPath(), $"lemonloader-patcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        try
        {
            var releaseArchive = options.ReleasePath ?? await ReleaseResolver.ResolveLatestAsync(Path.Combine(Path.GetDirectoryName(options.OutputPath)!, ".tools"));
            RequireFile(releaseArchive, "LemonLoader Release");
            var releaseRoot = Path.Combine(workRoot, "release");
            ZipFile.ExtractToDirectory(releaseArchive, releaseRoot);
            ValidateRelease(releaseRoot);

            var inputRoot = Path.Combine(workRoot, "interop-input");
            Directory.CreateDirectory(inputRoot);
            await ExtractInteropInputsAsync(inputRoot);
            var unityVersion = options.UnityVersion ?? DetectUnityVersion(Path.Combine(inputRoot, "globalgamemanagers"));
            if (string.IsNullOrWhiteSpace(unityVersion))
                throw new InvalidOperationException("Unity version was not supplied and could not be detected from globalgamemanagers.");

            var interopRoot = options.InteropOutputPath ?? Path.Combine(workRoot, "interop");
            await GenerateInteropAsync(inputRoot, interopRoot, unityVersion);

            var unsignedApk = Path.Combine(workRoot, "unsigned.apk");
            File.Copy(options.ApkPath, unsignedApk, true);
            MergeZip(unsignedApk, releaseRoot, interopRoot, options.ModPaths);
            await FinalizeApkAsync(unsignedApk, workRoot);
            Console.WriteLine($"Patched APK: {options.OutputPath}");
            Console.WriteLine($"SHA-256: {Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(options.OutputPath))).ToLowerInvariant()}");
        }
        finally
        {
            Directory.Delete(workRoot, true);
        }
    }

    private async Task ExtractInteropInputsAsync(string outputRoot)
    {
        using var apk = ZipFile.OpenRead(options.ApkPath);
        if (apk.GetEntry(MainEntry) is null || apk.GetEntry("lib/arm64-v8a/libunity.so") is null)
            throw new InvalidOperationException("The APK does not use the standard ARM64 Unity libmain.so startup layout.");
        await CopyInputAsync(apk, options.LibIl2CppPath, Il2CppEntry, Path.Combine(outputRoot, "libil2cpp.so"));
        await CopyInputAsync(apk, options.MetadataPath, MetadataEntry, Path.Combine(outputRoot, "global-metadata.dat"));
        var managers = apk.GetEntry(ManagersEntry);
        if (managers is not null) await ExtractEntryAsync(managers, Path.Combine(outputRoot, "globalgamemanagers"));
    }

    private static async Task CopyInputAsync(ZipArchive apk, string? explicitPath, string entryName, string destination)
    {
        if (explicitPath is not null) { RequireFile(explicitPath, entryName); File.Copy(explicitPath, destination, true); return; }
        var entry = apk.GetEntry(entryName) ?? throw new InvalidOperationException($"APK entry '{entryName}' was not found. Supply it explicitly.");
        await ExtractEntryAsync(entry, destination);
    }

    private async Task GenerateInteropAsync(string inputRoot, string outputRoot, string unityVersion)
    {
        Directory.CreateDirectory(outputRoot);
        var cpp2Il = options.Cpp2IlPath ?? await Cpp2IlResolver.ResolveAsync(Path.Combine(Path.GetDirectoryName(options.OutputPath)!, ".tools"));
        var dummyRoot = Path.Combine(Path.GetDirectoryName(outputRoot)!, ".cpp2il");
        Directory.CreateDirectory(dummyRoot);
        await ProcessRunner.RunAsync(cpp2Il,
            "--game-path", inputRoot, "--force-binary-path", Path.Combine(inputRoot, "libil2cpp.so"),
            "--force-metadata-path", Path.Combine(inputRoot, "global-metadata.dat"), "--force-unity-version", unityVersion,
            "--output-as", "dummydll", "--output-to", dummyRoot, "--use-processor", "attributeanalyzer,attributeinjector");
        await ProcessRunner.RunAsync("dotnet", "tool", "restore", "--tool-manifest", ToolManifest.Path,
            "--add-source", "https://nuget.bepinex.dev/v3/index.json");
        await ProcessRunner.RunAsync("dotnet", Path.GetFullPath(ToolManifest.FindToolDll()),
            "generate", "--input", dummyRoot, "--output", outputRoot, "--no-xref-cache", "--use-opt-out-prefixing");
        NormalizeInteropDirectory(outputRoot);
    }

    private static void MergeZip(string apkPath, string releaseRoot, string interopRoot, IReadOnlyList<string> modPaths)
    {
        using var archive = ZipFile.Open(apkPath, ZipArchiveMode.Update);
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.StartsWith("assets/dotnet/", StringComparison.Ordinal) ||
                     entry.FullName.StartsWith("assets/MelonLoader/", StringComparison.Ordinal)).ToArray()) entry.Delete();
        AddTree(archive, Path.Combine(releaseRoot, "assets"), "assets");
        AddTree(archive, Path.Combine(releaseRoot, "lib"), "lib");
        foreach (var dll in Directory.GetFiles(interopRoot, "*.dll"))
            AddFile(archive, dll, $"assets/MelonLoader/Il2CppAssemblies/{Path.GetFileName(dll)}");
        foreach (var mod in modPaths)
        {
            RequireFile(mod, "Mod assembly");
            AddFile(archive, mod, $"assets/MelonLoader/Mods/{Path.GetFileName(mod)}");
        }
    }

    private async Task FinalizeApkAsync(string unsignedApk, string workRoot)
    {
        var aligned = Path.Combine(workRoot, "aligned.apk");
        var needsAlignment = options.Align || options.KeystorePath is not null;
        var sign = options.KeystorePath is not null;
        if (!needsAlignment) { File.Copy(unsignedApk, options.OutputPath, true); return; }
        var tools = AndroidBuildTools.Resolve(options.AndroidSdkRoot);
        await ProcessRunner.RunAsync(tools.ZipAlign, "-P", "16", "-f", "4", unsignedApk, aligned);
        if (!sign) { File.Copy(aligned, options.OutputPath, true); return; }
        if (options.KeystorePassword is null || options.KeyAlias is null)
            throw new InvalidOperationException("Signing requires --ks-pass and --ks-alias.");
        await ProcessRunner.RunAsync(tools.ApkSigner, "sign", "--ks", options.KeystorePath!, "--ks-key-alias", options.KeyAlias,
            "--ks-pass", $"pass:{options.KeystorePassword}", "--key-pass", $"pass:{options.KeyPassword ?? options.KeystorePassword}",
            "--v4-signing-enabled", "false", "--out", options.OutputPath, aligned);
        await ProcessRunner.RunAsync(tools.ZipAlign, "-P", "16", "-c", "4", options.OutputPath);
        await ProcessRunner.RunAsync(tools.ApkSigner, "verify", "--verbose", options.OutputPath);
    }

    private static void ValidateRelease(string root)
    {
        var manifest = Path.Combine(root, "lemonloader-release.json");
        RequireFile(manifest, "release manifest");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        if (document.RootElement.GetProperty("gameAssembliesIncluded").GetBoolean())
            throw new InvalidOperationException("The LemonLoader Release contains game-specific assemblies.");
        RequireFile(Path.Combine(root, MainEntry.Replace('/', Path.DirectorySeparatorChar)), "release libmain.so");
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

    private static void AddFile(ZipArchive archive, string path, string name)
    {
        archive.GetEntry(name)?.Delete();
        archive.CreateEntryFromFile(path, name,
            name.StartsWith("lib/", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
    }

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string destination)
    {
        await using var input = entry.Open(); await using var output = File.Create(destination); await input.CopyToAsync(output);
    }

    private static void NormalizeInteropDirectory(string directoryPath)
    {
        var assemblyPaths = Directory.GetFiles(directoryPath, "*.dll");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(directoryPath);
        foreach (var path in assemblyPaths)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
            var changed = false;
            foreach (var type in assembly.MainModule.Types.SelectMany(EnumerateTypes))
            foreach (var method in type.Methods)
            foreach (var parameter in method.Parameters)
            {
                if (!parameter.HasDefault || parameter.HasConstant) continue;
                parameter.Attributes &= ~Mono.Cecil.ParameterAttributes.HasDefault;
                changed = true;
            }
            if (changed) assembly.Write(path + ".patched");
            if (changed) File.Move(path + ".patched", path, true);
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
    public static async Task RunAsync(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"'{fileName}' failed with exit code {process.ExitCode}.");
    }
}

internal static class ToolManifest
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, ".config", "dotnet-tools.json");
    public static string FindToolDll()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var store = System.IO.Path.Combine(home, ".nuget", "packages", "il2cppinterop.cli", "1.5.1-ci.845");
        return Directory.GetFiles(store, "Il2CppInterop.CLI.dll", SearchOption.AllDirectories).Single();
    }
}

internal static class Cpp2IlResolver
{
    private const string Version = "2022.1.0-pre-release.21";
    private static readonly Dictionary<string, string> Hashes = new(StringComparer.Ordinal)
    {
        ["Windows.exe"] = "663fb432433b4371fd1ee0ebc321a8fff2a9aac5ac4230c843f9e03ddee4e04c",
        ["Linux"] = "526998e593c52c029c5a6215c5c6c9f9d963706bfc409fc9ff80a95c4c500349",
        ["Linux-ARM64"] = "2d8e76b11f52fd85440ec60abf87d5c3455ea54f5104e0af51da15ba4440871b",
        ["OSX"] = "15faab020698512807f792aef32a89fe529d41d2cc03955dd3516f0519fe8f72",
        ["OSX-ARM64"] = "6670ddb93f28d4e7f329251d2597d2abbc6ba23e46382f7ca7b5e437ec93606a"
    };

    public static async Task<string> ResolveAsync(string root)
    {
        var suffix = OperatingSystem.IsWindows() ? "Windows.exe" : OperatingSystem.IsLinux()
            ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "Linux-ARM64" : "Linux")
            : (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "OSX-ARM64" : "OSX");
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, $"Cpp2IL-{Version}-{suffix}");
        if (!File.Exists(path))
        {
            using var client = new HttpClient();
            await File.WriteAllBytesAsync(path, await client.GetByteArrayAsync($"https://github.com/SamboyCoding/Cpp2IL/releases/download/{Version}/Cpp2IL-{Version}-{suffix}"));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        if (!string.Equals(hash, Hashes[suffix], StringComparison.Ordinal)) throw new InvalidOperationException("Cpp2IL SHA-256 mismatch.");
        return path;
    }
}

internal static class ReleaseResolver
{
    private const string LatestUrl = "https://github.com/LemonLoader/MelonLoader/releases/latest/download/LemonLoader-Android-arm64.zip";

    public static async Task<string> ResolveLatestAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "LemonLoader-Android-arm64.zip");
        using var client = new HttpClient();
        await File.WriteAllBytesAsync(path, await client.GetByteArrayAsync(LatestUrl));
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
