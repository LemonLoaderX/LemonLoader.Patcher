using System.Security.Cryptography;
using System.Text.Json;

namespace LemonLoader.Patcher.Core;

internal static class ReleaseValidator
{
    private const string ManifestName = "lemonloader-release.json";
    private const string MainLibraryPath = "lib/arm64-v8a/libmain.so";
    private const string PayloadManifestPath = AndroidPayloadContract.PayloadManifestPath;
    private const int AssetLayoutVersion = AndroidPayloadContract.FormatVersion;

    public static ReleaseValidationResult Validate(string releaseRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(releaseRoot);
        var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Release manifest was not found.", manifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.GetProperty("formatVersion").GetInt32() != 2)
            throw new InvalidDataException("Unsupported LemonLoader Release manifest format.");
        var layoutVersion = manifest.GetProperty("assetLayoutVersion").GetInt32();
        if (layoutVersion != AssetLayoutVersion)
            throw new InvalidDataException("Unsupported LemonLoader Android asset layout.");
        if (manifest.GetProperty("gameAssembliesIncluded").GetBoolean())
            throw new InvalidDataException("The LemonLoader Release contains game-specific assemblies.");

        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        var verifiedFiles = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
        foreach (var entry in manifest.GetProperty("files").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = entry.GetProperty("path").GetString()
                ?? throw new InvalidDataException("A Release manifest file path is null.");
            var normalizedPath = ValidateRelativePath(root, relativePath);
            if (!expectedFiles.Add(relativePath))
                throw new InvalidDataException($"Release manifest contains duplicate path '{relativePath}'.");
            if (!File.Exists(normalizedPath))
                throw new InvalidDataException($"Release file '{relativePath}' is missing.");

            var expectedSize = entry.GetProperty("size").GetInt64();
            var actualSize = new FileInfo(normalizedPath).Length;
            if (actualSize != expectedSize)
                throw new InvalidDataException(
                    $"Release file '{relativePath}' has size {actualSize}, expected {expectedSize}.");

            var expectedHash = entry.GetProperty("sha256").GetString();
            if (expectedHash is null || expectedHash.Length != 64 ||
                expectedHash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"Release file '{relativePath}' has an invalid SHA-256 value.");
            }

            using var input = File.OpenRead(normalizedPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Release file '{relativePath}' failed SHA-256 validation.");
            verifiedFiles.Add(relativePath, (actualSize, actualHash));
        }

        if (!expectedFiles.Contains(MainLibraryPath) || !expectedFiles.Contains(PayloadManifestPath))
            throw new InvalidDataException("Release manifest does not contain the Android bootstrap and payload manifest.");

        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !string.Equals(path, ManifestName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            var unexpected = actualFiles.Except(expectedFiles, StringComparer.Ordinal).Order().ToArray();
            var missing = expectedFiles.Except(actualFiles, StringComparer.Ordinal).Order().ToArray();
            throw new InvalidDataException(
                $"Release payload does not match its manifest. " +
                $"Unexpected: [{string.Join(", ", unexpected)}]; missing: [{string.Join(", ", missing)}].");
        }

        ValidateAndroidLayout(root, actualFiles, manifest, verifiedFiles);
        return new ReleaseValidationResult(root);
    }

    private static void ValidateAndroidLayout(
        string root,
        IReadOnlySet<string> files,
        JsonElement releaseManifest,
        IReadOnlyDictionary<string, (long Size, string Hash)> verifiedFiles)
    {
        var runtimeVersion = releaseManifest.GetProperty("managedRuntimeVersion").GetString();
        var configuration = releaseManifest.GetProperty("configuration").GetString();
        var runtimeRevision = releaseManifest.GetProperty("managedRuntimeSourceRevision").GetString();
        var runtimeEngineFile = releaseManifest.GetProperty("managedRuntimeEngineFile").GetString();
        var runtimeEngineHash = releaseManifest.GetProperty("managedRuntimeEngineSha256").GetString();
        if (string.IsNullOrWhiteSpace(runtimeVersion) ||
            configuration is not ("Debug" or "Release") ||
            runtimeRevision is null || runtimeRevision.Length != 40 ||
            runtimeRevision.Any(character => !Uri.IsHexDigit(character)) ||
            runtimeEngineFile != "libcoreclr.so" ||
            runtimeEngineHash is null || !IsSha256(runtimeEngineHash))
            throw new InvalidDataException("The Release does not contain valid managed runtime source provenance.");

        var cryptoDexMode = AndroidPayloadContract.ReadCryptoDexMode(releaseManifest);
        if (releaseManifest.GetProperty("managedRuntimeBackend").GetString() != "coreclr" ||
            !releaseManifest.TryGetProperty("runtimeRid", out var runtimeRid) ||
            runtimeRid.GetString() is not ("android-arm64" or "linux-bionic-arm64") ||
            !releaseManifest.TryGetProperty("minimumAndroidApi", out var minimumApi) ||
            minimumApi.ValueKind != JsonValueKind.Number || !minimumApi.TryGetInt32(out var api) || api < 26 ||
            (runtimeRid.GetString() == "android-arm64" ? cryptoDexMode != "embedded" : cryptoDexMode is not null))
            throw new InvalidDataException("Layout 9 requires an API26+ CoreCLR runtime with matching cryptography.");

        var invalidAsset = files.FirstOrDefault(path =>
            path.StartsWith("assets/", StringComparison.Ordinal) &&
            !path.StartsWith(AndroidPayloadContract.PayloadRoot + "/", StringComparison.Ordinal));
        if (invalidAsset is not null)
            throw new InvalidDataException($"Release asset '{invalidAsset}' is outside the consolidated assets/LemonLoader tree.");
        var publicNativeLibraries = files.Where(path =>
            path.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal)).ToArray();
        if (publicNativeLibraries.Length != 1 || publicNativeLibraries[0] != MainLibraryPath)
            throw new InvalidDataException(
                "The public APK native payload must contain only libmain.so; runtime dependencies belong in private assets.");

        using var payloadDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, PayloadManifestPath.Replace('/', Path.DirectorySeparatorChar))));
        var payload = payloadDocument.RootElement;
        if (payload.GetProperty("formatVersion").GetInt32() != AssetLayoutVersion ||
            !payload.TryGetProperty("runtimeRid", out var declaredRid) ||
            declaredRid.GetString() != runtimeRid.GetString())
            throw new InvalidDataException("Runtime RID or layout does not match the payload.");
        var invalidRuntimePath = files.FirstOrDefault(path =>
            path.StartsWith(AndroidPayloadContract.PayloadRoot + "/runtime/", StringComparison.Ordinal) &&
            !AndroidPayloadContract.IsRuntimeDomainPath(path));
        if (invalidRuntimePath is not null)
            throw new InvalidDataException($"Android runtime file '{invalidRuntimePath}' is outside a supported update domain.");
        if (files.Any(path => path.StartsWith(AndroidPayloadContract.DeploymentRoot + "/", StringComparison.Ordinal) ||
                              path.StartsWith(AndroidPayloadContract.InteropRoot + "/", StringComparison.Ordinal)) ||
            (payload.TryGetProperty("deploymentFiles", out var deploymentFiles) &&
             (deploymentFiles.ValueKind != JsonValueKind.Array || deploymentFiles.GetArrayLength() != 0)))
            throw new InvalidDataException("A game-independent Release contains deployment or game Interop inputs.");

        var sharedRuntimeRoot = $"{AndroidPayloadContract.DotnetRoot}/shared/Microsoft.NETCore.App/{runtimeVersion}";
        var enginePath = $"{sharedRuntimeRoot}/{runtimeEngineFile}";
        if (!verifiedFiles.TryGetValue(enginePath, out var engine) || engine.Size == 0 ||
            !string.Equals(engine.Hash, runtimeEngineHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Release managed runtime engine is missing or differs from its source provenance.");
        foreach (var name in new[] { "System.Private.CoreLib.dll", "libclrjit.so" })
            RequireRuntimeFile($"{sharedRuntimeRoot}/{name}");
        foreach (var name in new[] { "MelonLoader.dll", "MelonLoader.NativeHost.dll" })
            RequireRuntimeFile($"{AndroidPayloadContract.LoaderRoot}/net6/{name}");
        RequireRuntimeFile($"{AndroidPayloadContract.LoaderRoot}/Dependencies/SupportModules/Il2Cpp.dll");

        var androidCrypto = $"{sharedRuntimeRoot}/libSystem.Security.Cryptography.Native.Android.so";
        var opensslCrypto = $"{sharedRuntimeRoot}/libSystem.Security.Cryptography.Native.OpenSsl.so";
        if (runtimeRid.GetString() == "linux-bionic-arm64")
        {
            if (files.Contains(androidCrypto))
                throw new InvalidDataException("Bionic runtime contains the Android JNI crypto library.");
            foreach (var name in new[] { "libSystem.Security.Cryptography.Native.OpenSsl.so", "libssl.so", "libcrypto.so" })
                RequireRuntimeFile($"{sharedRuntimeRoot}/{name}");
            RequireRuntimeFile("licenses/OpenSSL/LICENSE.txt");
        }
        else
        {
            if (files.Contains(opensslCrypto))
                throw new InvalidDataException("Android runtime contains the Bionic OpenSSL crypto library.");
            RequireRuntimeFile(androidCrypto);
        }

        void RequireRuntimeFile(string path)
        {
            if (!verifiedFiles.TryGetValue(path, out var file) || file.Size == 0)
                throw new InvalidDataException($"The runtime Release is missing or has an empty required file '{path}'.");
        }
    }

    private static string ValidateRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\'))
        {
            throw new InvalidDataException($"Release manifest path '{relativePath}' is not a normalized relative path.");
        }

        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Release manifest path '{relativePath}' contains an unsafe segment.");

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException($"Release manifest path '{relativePath}' escapes the Release root.");
        return fullPath;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

internal sealed record ReleaseValidationResult(string ReleaseRoot);
