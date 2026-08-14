using System.Security.Cryptography;
using System.Text.Json;

internal static class ReleaseValidator
{
    private const string ManifestName = "lemonloader-release.json";
    private const string MainLibraryPath = "lib/arm64-v8a/libmain.so";
    private const string PayloadManifestPath = "assets/LemonLoader/payload.json";
    private const int AssetLayoutVersion = 4;

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

        ValidateAndroidLayout(root, actualFiles);
    }

    private static void ValidateAndroidLayout(string root, IReadOnlySet<string> files)
    {
        var invalidAsset = files.FirstOrDefault(path =>
            path.StartsWith("assets/", StringComparison.Ordinal) &&
            !path.StartsWith("assets/LemonLoader/", StringComparison.Ordinal));
        if (invalidAsset is not null)
            throw new InvalidDataException(
                $"Release asset '{invalidAsset}' is outside the consolidated assets/LemonLoader tree.");

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
        foreach (var propertyName in new[] { "runtimeSha256", "deploymentSha256" })
        {
            var hash = payload.GetProperty(propertyName).GetString();
            if (hash is null || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"The Android payload manifest property '{propertyName}' is not SHA-256.");
            var scope = propertyName == "runtimeSha256" ? "runtime" : "deployment";
            var actualHash = ComputePayloadTreeHash(root, scope);
            if (!string.Equals(hash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The Android payload manifest hash for '{scope}' does not match its files.");
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

    private static string ComputePayloadTreeHash(string releaseRoot, string scope)
    {
        var payloadRoot = Path.Combine(releaseRoot, "assets", "LemonLoader");
        var scopeRoot = Path.Combine(payloadRoot, scope);
        var filePaths = Directory.Exists(scopeRoot)
            ? Directory.EnumerateFiles(scopeRoot, "*", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
        var files = filePaths.Select(path => new
            {
                Path = path,
                RelativePath = Path.GetRelativePath(payloadRoot, path).Replace('\\', '/')
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal);
        var lines = new List<string> { $"layout-version={AssetLayoutVersion}", $"scope={scope}" };
        foreach (var file in files)
        {
            using var input = File.OpenRead(file.Path);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            lines.Add($"{file.RelativePath}|{input.Length}|{hash}");
        }
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
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
