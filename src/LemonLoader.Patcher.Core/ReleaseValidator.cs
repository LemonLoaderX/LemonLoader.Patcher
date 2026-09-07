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
        if (manifest.GetProperty("formatVersion").GetInt32() != 2)
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
        var runtimeVersion = releaseManifest.GetProperty("managedRuntimeVersion").GetString();
        var configuration = releaseManifest.GetProperty("configuration").GetString();
        var runtimeBackendName = releaseManifest.GetProperty("managedRuntimeBackend").GetString();
        var runtimeBackend = AndroidPayloadContract.ParseManagedRuntimeBackend(runtimeBackendName);
        var runtimeHostingModel = runtimeBackend == ManagedRuntimeBackend.CoreClr
            ? "coreclr-host-api"
            : "hostfxr";
        var runtimeRevision = releaseManifest.GetProperty("managedRuntimeSourceRevision").GetString();
        var runtimeEngineFile = releaseManifest.GetProperty("managedRuntimeEngineFile").GetString();
        var runtimeEngineHash = releaseManifest.GetProperty("managedRuntimeEngineSha256").GetString();
        var runtimeThreadFilterAvailable = false;
        if (releaseManifest.TryGetProperty("managedRuntimeThreadFilterAvailable", out var threadFilter))
        {
            if (threadFilter.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException(
                    "The managed runtime thread-filter capability must be a boolean.");
            runtimeThreadFilterAvailable = threadFilter.GetBoolean();
        }
        if (string.IsNullOrWhiteSpace(runtimeVersion) ||
            configuration is not ("Debug" or "Release") ||
            runtimeRevision is null || runtimeRevision.Length != 40 ||
            runtimeRevision.Any(character => !Uri.IsHexDigit(character)) ||
            runtimeEngineFile != "libcoreclr.so" ||
            runtimeEngineHash is null || runtimeEngineHash.Length != 64 ||
            runtimeEngineHash.Any(character => !Uri.IsHexDigit(character)) ||
            (runtimeBackend == ManagedRuntimeBackend.MonoVmSgen && !runtimeThreadFilterAvailable) ||
            (runtimeBackend == ManagedRuntimeBackend.CoreClr && runtimeThreadFilterAvailable))
        {
            throw new InvalidDataException(
                "The Release does not contain valid managed runtime source provenance.");
        }
        var runtimeEnginePath =
            $"assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/{runtimeVersion}/{runtimeEngineFile}";
        if (!files.Contains(runtimeEnginePath))
            throw new InvalidDataException("The Release managed runtime engine is missing.");
        using (var input = File.OpenRead(Path.Combine(
                   root,
                   runtimeEnginePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            var actualRuntimeEngineHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!string.Equals(
                    runtimeEngineHash,
                    actualRuntimeEngineHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The Release managed runtime engine hash does not match its source provenance.");
            }
        }

        var runtimeIdentityPath =
            $"{AndroidPayloadContract.DotnetRoot}/runtime-identity.json";
        if (!files.Contains(runtimeIdentityPath))
            throw new InvalidDataException("The Release managed runtime identity file is missing.");
        var runtimeIdentityFile = Path.Combine(
            root,
            runtimeIdentityPath.Replace('/', Path.DirectorySeparatorChar));
        using (var runtimeIdentityDocument = JsonDocument.Parse(
                   File.ReadAllText(runtimeIdentityFile)))
        {
            var identity = runtimeIdentityDocument.RootElement;
            if (releaseManifest.TryGetProperty("runtimeRid", out var identityRid))
            {
                var cryptoBackend = identityRid.GetString() == "linux-bionic-arm64" ? "openssl" : "android-jni";
                if (!identity.TryGetProperty("runtimeRid", out var actualRid) ||
                    actualRid.GetString() != identityRid.GetString() ||
                    !identity.TryGetProperty("cryptoBackend", out var actualCrypto) || actualCrypto.GetString() != cryptoBackend)
                    throw new InvalidDataException("Runtime RID/cryptography identity differs from the Release.");
            }
            if (identity.GetProperty("formatVersion").GetInt32() != 1 ||
                identity.GetProperty("runtimeVersion").GetString() != runtimeVersion ||
                identity.GetProperty("backend").GetString() != runtimeBackendName ||
                identity.GetProperty("hostingModel").GetString() != runtimeHostingModel ||
                identity.GetProperty("engineFile").GetString() != runtimeEngineFile ||
                !string.Equals(
                    identity.GetProperty("engineSha256").GetString(),
                    runtimeEngineHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The managed runtime identity file does not match the Release manifest.");
            }
        }

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
        var payloadRuntimeBackend = AndroidPayloadContract.ParseManagedRuntimeBackend(
            payload.GetProperty("managedRuntimeBackend").GetString());
        var payloadRuntimeIdentityHash =
            payload.GetProperty("managedRuntimeIdentitySha256").GetString();
        using (var input = File.OpenRead(runtimeIdentityFile))
        {
            var actualRuntimeIdentityHash =
                Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (payloadRuntimeBackend != runtimeBackend ||
                payloadRuntimeIdentityHash is null ||
                payloadRuntimeIdentityHash.Length != 64 ||
                payloadRuntimeIdentityHash.Any(character => !Uri.IsHexDigit(character)) ||
                !string.Equals(
                    payloadRuntimeIdentityHash,
                    actualRuntimeIdentityHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The payload managed runtime identity does not match the Release file.");
            }
        }
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

        var privateLibrariesProperty = payload.GetProperty("privateNativeLibraries");
        if (privateLibrariesProperty.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "The Android payload manifest private native library list is not an array.");
        var privateLibraries = privateLibrariesProperty
            .EnumerateArray()
            .Select(value => value.GetString() ?? throw new InvalidDataException(
                "The Android payload manifest contains a null private native library name."))
            .ToHashSet(StringComparer.Ordinal);
        var sharedRuntimeRoot =
            $"assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/{runtimeVersion}";
        var androidCryptoPath =
            $"{sharedRuntimeRoot}/libSystem.Security.Cryptography.Native.Android.so";
        var androidCryptoDexPath = AndroidPayloadContract.CoreClrCryptoDexReleasePath;
        var privateOpenSslRoot = "assets/LemonLoader/runtime/dotnet/native/openssl/";
        var coreClrCryptoDexHash = payload.GetProperty("coreClrCryptoDexSha256");
        var hasRuntimeRid = releaseManifest.TryGetProperty("runtimeRid", out var runtimeRid);
        if (releaseManifest.TryGetProperty("experimentalRuntimeRid", out var oldRid) &&
            (oldRid.GetString() != "linux-bionic-arm64" ||
             (hasRuntimeRid && runtimeRid.GetString() != oldRid.GetString())))
            throw new InvalidDataException("Conflicting runtime RID metadata.");
        if (hasRuntimeRid)
        {
            if (runtimeRid.GetString() is not ("android-arm64" or "linux-bionic-arm64") ||
                !payload.TryGetProperty("runtimeRid", out var declaredRid) ||
                declaredRid.GetString() != runtimeRid.GetString())
                throw new InvalidDataException("Runtime RID metadata does not match the payload.");
        }
        if ((hasRuntimeRid && runtimeRid.GetString() == "linux-bionic-arm64") ||
            releaseManifest.TryGetProperty("experimentalRuntimeRid", out _))
        {
            var validRid = hasRuntimeRid ||
                (releaseManifest.GetProperty("experimentalRuntimeRid").GetString() == "linux-bionic-arm64" &&
                 payload.TryGetProperty("experimentalRuntimeRid", out var payloadRid) &&
                 payloadRid.GetString() == "linux-bionic-arm64");
            if (!validRid ||
                runtimeBackend != ManagedRuntimeBackend.CoreClr ||
                coreClrCryptoDexHash.ValueKind != JsonValueKind.Null ||
                files.Contains(androidCryptoPath) || files.Contains(androidCryptoDexPath))
                throw new InvalidDataException("Invalid experimental Bionic runtime contract.");
            foreach (var name in new[] { "libSystem.Security.Cryptography.Native.OpenSsl.so", "libssl.so", "libcrypto.so" })
            {
                if (!files.Contains($"{sharedRuntimeRoot}/{name}"))
                    throw new InvalidDataException($"Experimental Bionic runtime is missing '{name}'.");
            }
            if (hasRuntimeRid && !files.Contains("licenses/OpenSSL/LICENSE.txt"))
                throw new InvalidDataException("Bionic runtime has no OpenSSL attribution.");
            return;
        }
        if (runtimeBackend == ManagedRuntimeBackend.CoreClr)
        {
            if (!files.Contains(androidCryptoPath) || !files.Contains(androidCryptoDexPath))
            {
                throw new InvalidDataException(
                    "The Android CoreCLR Release is missing its source-built crypto library or helper dex.");
            }
            var expectedCryptoDexHash = coreClrCryptoDexHash.GetString();
            if (expectedCryptoDexHash is null || !IsSha256(expectedCryptoDexHash))
                throw new InvalidDataException(
                    "The Android CoreCLR payload has an invalid crypto helper dex hash.");
            using (var input = File.OpenRead(Path.Combine(
                       root,
                       androidCryptoDexPath.Replace('/', Path.DirectorySeparatorChar))))
            {
                var actualCryptoDexHash =
                    Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (!string.Equals(
                        expectedCryptoDexHash,
                        actualCryptoDexHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The Android CoreCLR crypto helper dex hash does not match payload.json.");
                }
            }
            if (privateLibraries.Contains("lemcrypto.so") ||
                privateLibraries.Contains("lemssl.so"))
                throw new InvalidDataException(
                    "The Android CoreCLR Release must not declare private OpenSSL dependencies.");

            return;
        }

        if (coreClrCryptoDexHash.ValueKind != JsonValueKind.Null ||
            files.Contains(androidCryptoDexPath))
        {
            throw new InvalidDataException(
                "The Android MonoVM/SGen Release contains CoreCLR crypto helper dex metadata.");
        }

        var expectedPrivateLibraries = new HashSet<string>(
            ["lemcrypto.so", "lemssl.so"],
            StringComparer.Ordinal);
        if (!expectedPrivateLibraries.IsSubsetOf(privateLibraries))
            throw new InvalidDataException(
                "The Android MonoVM/SGen Release must declare its isolated OpenSSL compatibility pair.");
        foreach (var library in expectedPrivateLibraries)
        {
            if (!files.Contains($"{privateOpenSslRoot}{library}"))
                throw new InvalidDataException(
                    $"The private Android OpenSSL dependency '{library}' is missing.");
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
