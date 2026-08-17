using System.Security.Cryptography;
using System.Text.Json;

internal static class ReleaseValidator
{
    private const string ManifestName = "lemonloader-release.json";
    private const string MainLibraryPath = "lib/arm64-v8a/libmain.so";
    private const string PayloadManifestPath = AndroidPayloadContract.PayloadManifestPath;
    private const int AssetLayoutVersion = AndroidPayloadContract.FormatVersion;

    public static void Validate(string releaseRoot)
    {
        var root = Path.GetFullPath(releaseRoot);
        var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Release manifest was not found.", manifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.GetProperty("formatVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported LemonLoader Release manifest format.");
        if (manifest.GetProperty("assetLayoutVersion").GetInt32() != AssetLayoutVersion)
            throw new InvalidDataException("Unsupported LemonLoader Android asset layout.");
        if (manifest.GetProperty("gameAssembliesIncluded").GetBoolean())
            throw new InvalidDataException("The LemonLoader Release contains game-specific assemblies.");

        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.GetProperty("files").EnumerateArray())
        {
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

        ValidateAndroidLayout(root, actualFiles, manifest);
    }

    private static void ValidateAndroidLayout(
        string root,
        IReadOnlySet<string> files,
        JsonElement releaseManifest)
    {
        var runtimeVersion = releaseManifest.GetProperty("dotnetRuntimeVersion").GetString();
        var runtimeRevision = releaseManifest.GetProperty("dotnetRuntimeRevision").GetString();
        var coreClrHash = releaseManifest.GetProperty("coreClrSha256").GetString();
        if (string.IsNullOrWhiteSpace(runtimeVersion) ||
            runtimeRevision is null || runtimeRevision.Length != 40 ||
            runtimeRevision.Any(character => !Uri.IsHexDigit(character)) ||
            coreClrHash is null || coreClrHash.Length != 64 ||
            coreClrHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The Release does not contain valid CoreCLR source provenance.");
        }
        var coreClrPath = $"assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/{runtimeVersion}/libcoreclr.so";
        if (!files.Contains(coreClrPath))
            throw new InvalidDataException("The Release CoreCLR file is missing.");
        using (var input = File.OpenRead(Path.Combine(
                   root,
                   coreClrPath.Replace('/', Path.DirectorySeparatorChar))))
        {
            var actualCoreClrHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!string.Equals(coreClrHash, actualCoreClrHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Release CoreCLR hash does not match its source provenance.");
        }

        var invalidAsset = files.FirstOrDefault(path =>
            path.StartsWith("assets/", StringComparison.Ordinal) &&
            !path.StartsWith("assets/LemonLoader/", StringComparison.Ordinal));
        if (invalidAsset is not null)
            throw new InvalidDataException(
                $"Release asset '{invalidAsset}' is outside the consolidated assets/LemonLoader tree.");

        var documentation = files.FirstOrDefault(AndroidPayloadContract.IsForbiddenReleasePath);
        if (documentation is not null)
            throw new InvalidDataException(
                $"Android Release must not contain loader documentation '{documentation}'.");

        var publicNativeLibraries = files
            .Where(path => path.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal))
            .ToArray();
        if (publicNativeLibraries.Length != 1 || publicNativeLibraries[0] != MainLibraryPath)
            throw new InvalidDataException(
                "The public APK native payload must contain only libmain.so; runtime dependencies belong in private assets.");

        using var payloadDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            PayloadManifestPath.Replace('/', Path.DirectorySeparatorChar))));
        var payload = payloadDocument.RootElement;
        if (payload.GetProperty("formatVersion").GetInt32() != AssetLayoutVersion)
            throw new InvalidDataException("The Android payload manifest has an unsupported format version.");
        var deploymentHash = payload.GetProperty("deploymentSha256").GetString();
        var actualDeploymentHash = AndroidPayloadContract.ComputeTreeHash(root, "deployment");
        if (deploymentHash is null || deploymentHash.Length != 64 ||
            deploymentHash.Any(character => !Uri.IsHexDigit(character)) ||
            !string.Equals(deploymentHash, actualDeploymentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Android payload manifest hash for 'deployment' is invalid.");
        }

        var invalidRuntimePath = files.FirstOrDefault(path =>
            path.StartsWith($"{AndroidPayloadContract.PayloadRoot}/runtime/", StringComparison.Ordinal) &&
            !AndroidPayloadContract.IsRuntimeDomainPath(path));
        if (invalidRuntimePath is not null)
            throw new InvalidDataException(
                $"Android runtime file '{invalidRuntimePath}' is outside a supported update domain.");

        foreach (var domain in new[]
                 {
                     (Property: "loaderSha256", Scope: "runtime/loader"),
                     (Property: "dotnetSha256", Scope: "runtime/dotnet"),
                     (Property: "interopSha256", Scope: "runtime/interop")
                 })
        {
            var property = payload.GetProperty(domain.Property);
            var hash = property.GetString();
            var actualHash = AndroidPayloadContract.ComputeTreeHash(root, domain.Scope);
            if (hash is null || hash.Length != 64 ||
                hash.Any(character => !Uri.IsHexDigit(character)) ||
                !string.Equals(hash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The Android payload manifest hash for '{domain.Scope}' is invalid.");
            }
        }

        if (payload.GetProperty("deploymentProfile").GetString() != "development" ||
            payload.GetProperty("deploymentFiles").GetArrayLength() != 0)
        {
            throw new InvalidDataException(
                "A game-independent Release must use the development profile and contain no deployment files.");
        }
        var emptyDeploymentRevision = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("deployment-revision=1")))
            .ToLowerInvariant();
        if (payload.GetProperty("deploymentRevisionSha256").GetString() !=
            emptyDeploymentRevision)
        {
            throw new InvalidDataException(
                "A game-independent Release has an invalid empty deployment revision.");
        }

        var privateLibraries = payload.GetProperty("privateNativeLibraries")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var library in new[] { "lemcrypto.so", "lemssl.so" })
        {
            if (!privateLibraries.Contains(library) ||
                !files.Contains($"assets/LemonLoader/runtime/dotnet/native/openssl/{library}"))
            {
                throw new InvalidDataException(
                    $"The private Android OpenSSL dependency '{library}' is missing or undeclared.");
            }
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
}
