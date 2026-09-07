using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record PayloadSource(
    string ReleaseRoot,
    string InteropRoot,
    string? DeploymentPath,
    DeploymentPolicyOptions DeploymentPolicies,
    IReadOnlyDictionary<string, (long Size, string Hash)> VerifiedReleaseFiles);

internal static class PayloadAssembler
{
    private const string PayloadEntry = AndroidPayloadContract.PayloadManifestPath;
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void MergeApk(string apkPath, PayloadSource source)
    {
        var payload = ReadPayloadDescriptor(GamePackageLayout.FilePath(
            source.ReleaseRoot,
            PayloadEntry));
        using var archive = ZipFile.Open(apkPath, ZipArchiveMode.Update);
        ValidateUniqueEntries(archive);
        ValidatePrivateNativeLibraries(archive, payload.PrivateNativeLibraries);
        RejectExistingLoaderPayload(archive);
        AddTree(archive, Path.Combine(source.ReleaseRoot, "assets"), "assets");
        var coreClrCryptoDexHash = AddCoreClrCryptoDex(archive, source, payload);
        ValidateRuntimeEntries(archive);
        AddNativeTree(archive, Path.Combine(source.ReleaseRoot, "lib"));
        foreach (var dll in Directory.GetFiles(source.InteropRoot, "*.dll"))
        {
            AddFile(
                archive,
                dll,
                $"{AndroidPayloadContract.InteropRoot}/{Path.GetFileName(dll)}");
        }
        var interopManifest = Path.Combine(
            source.InteropRoot,
            InteropGenerationManifest.FileName);
        RequireFile(interopManifest, "Interop generation manifest");
        AddFile(
            archive,
            interopManifest,
            $"{AndroidPayloadContract.InteropRoot}/{InteropGenerationManifest.FileName}");
        if (source.DeploymentPath is not null)
            AddDeploymentRoot(archive, source.DeploymentPath);
        ValidateDeploymentEntries(archive);
        RefreshPayloadDescriptor(
            archive,
            payload,
            source.DeploymentPolicies,
            coreClrCryptoDexHash);
        ValidateUniqueEntries(archive);
    }

    public static void InjectDirectory(
        string gameRoot,
        PayloadSource source,
        IProgress<PatcherMessage>? progress)
    {
        gameRoot = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException($"Input directory was not found at '{gameRoot}'.");
        if (!File.Exists(GamePackageLayout.FilePath(
                gameRoot,
                GamePackageLayout.MainLibrary)) ||
            !File.Exists(GamePackageLayout.FilePath(
                gameRoot,
                GamePackageLayout.UnityLibrary)))
        {
            throw new InvalidOperationException(
                "The input directory does not use the standard ARM64 Unity libmain.so startup layout.");
        }
        RejectExistingLoaderPayload(gameRoot);

        var workRoot = Path.Combine(
            Path.GetTempPath(),
            $"lemonloader-directory-patch-{Guid.NewGuid():N}");
        var stagingApk = Path.Combine(workRoot, "payload.apk");
        var overlayRoot = Path.Combine(workRoot, "overlay");
        Directory.CreateDirectory(workRoot);
        try
        {
            var originalEntries = CreateDirectorySeedApk(gameRoot, stagingApk);
            MergeApk(stagingApk, source);
            ExtractDirectoryOverlay(stagingApk, overlayRoot, originalEntries);
            DirectoryInjector.Apply(gameRoot, overlayRoot, progress);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                    Directory.Delete(workRoot, true);
            }
            catch (Exception cleanupException) when (progress is not null)
            {
                progress.Report(new(
                    PatcherMessageKind.Warning,
                    $"Could not remove temporary directory '{workRoot}': {cleanupException.Message}"));
            }
        }
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

    private static IReadOnlySet<string> CreateDirectorySeedApk(
        string gameRoot,
        string apkPath)
    {
        var nativeRoot = GamePackageLayout.FilePath(
            gameRoot,
            GamePackageLayout.Arm64LibraryRoot);
        var entryNames = Directory.GetFiles(nativeRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(path => $"{GamePackageLayout.Arm64LibraryRoot}/{Path.GetFileName(path)}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var dexPath in Directory.GetFiles(gameRoot, "classes*.dex", SearchOption.TopDirectoryOnly))
            entryNames.Add(Path.GetFileName(dexPath));
        AddDecodedDexEntries(gameRoot, entryNames);
        using var archive = ZipFile.Open(apkPath, ZipArchiveMode.Create);
        foreach (var entryName in entryNames.OrderBy(name => name, StringComparer.Ordinal))
            archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        return entryNames;
    }

    private static void AddDecodedDexEntries(string gameRoot, ISet<string> entryNames)
    {
        const string secondaryPrefix = "smali_classes";
        foreach (var directory in Directory.GetDirectories(
                     gameRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "smali", StringComparison.Ordinal))
            {
                entryNames.Add("classes.dex");
                continue;
            }
            if (!name.StartsWith(secondaryPrefix, StringComparison.Ordinal))
                continue;

            var suffix = name[secondaryPrefix.Length..];
            if (suffix.Length == 0 || suffix[0] == '0' ||
                !int.TryParse(
                    suffix,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index) ||
                index < 2)
            {
                continue;
            }
            entryNames.Add($"classes{index}.dex");
        }
    }

    private static void ExtractDirectoryOverlay(
        string apkPath,
        string overlayRoot,
        IReadOnlySet<string> originalEntries)
    {
        Directory.CreateDirectory(overlayRoot);
        using var archive = ZipFile.OpenRead(apkPath);
        var resolvedRoot = Path.GetFullPath(overlayRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            if (originalEntries.Contains(entry.FullName) &&
                entry.FullName != GamePackageLayout.MainLibrary)
            {
                continue;
            }
            var destination = GamePackageLayout.FilePath(overlayRoot, entry.FullName);
            if (!Path.GetFullPath(destination).StartsWith(resolvedRoot, PathComparison))
            {
                throw new InvalidDataException(
                    $"Payload entry '{entry.FullName}' escapes the staging directory.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }
    }

    private static void RefreshPayloadDescriptor(
        ZipArchive archive,
        PayloadDescriptor descriptor,
        DeploymentPolicyOptions deploymentPolicies,
        string? coreClrCryptoDexHash)
    {
        var deploymentFiles = BuildDeploymentFileDescriptors(archive, deploymentPolicies);
        deploymentPolicies.ValidateRuleCoverage(deploymentFiles.Select(file => file.Path));
        var updated = descriptor with
        {
            LoaderSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime/loader"),
            DotnetSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime/dotnet"),
            InteropSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "runtime/interop"),
            DeploymentSha256 = AndroidPayloadContract.ComputeTreeHash(archive, "deployment"),
            DeploymentProfile = deploymentPolicies.Profile.ToString().ToLowerInvariant(),
            DeploymentRevisionSha256 = ComputeDeploymentRevision(deploymentFiles),
            DeploymentFiles = deploymentFiles,
            CoreClrCryptoDexSha256 = coreClrCryptoDexHash
        };
        archive.GetEntry(PayloadEntry)?.Delete();
        var manifestEntry = archive.CreateEntry(PayloadEntry, CompressionLevel.Optimal);
        using var output = manifestEntry.Open();
        JsonSerializer.Serialize(output, updated, PayloadJsonOptions);
    }

    private static IReadOnlyList<DeploymentFileDescriptor> BuildDeploymentFileDescriptors(
        ZipArchive archive,
        DeploymentPolicyOptions deploymentPolicies)
    {
        const string prefix = AndroidPayloadContract.DeploymentRoot + "/";
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
                var path = entry.FullName[prefix.Length..];
                return new DeploymentFileDescriptor(
                    path,
                    length,
                    Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
                    DeploymentPolicyOptions.ToManifestValue(deploymentPolicies.Resolve(path)));
            })
            .ToArray();
    }

    private static PayloadDescriptor ReadPayloadDescriptor(string path)
    {
        RequireFile(path, "Android payload manifest");
        var descriptor = JsonSerializer.Deserialize<PayloadDescriptor>(
            File.ReadAllText(path),
            PayloadJsonOptions) ?? throw new InvalidDataException(
                "Android payload manifest is empty.");
        if (descriptor.FormatVersion != AndroidPayloadContract.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Android payload layout {descriptor.FormatVersion}; " +
                $"expected {AndroidPayloadContract.FormatVersion}.");
        }
        if (descriptor.DeploymentFiles is null ||
            descriptor.DeploymentProfile is null ||
            descriptor.DeploymentRevisionSha256 is null ||
            descriptor.ManagedRuntimeBackend is null ||
            descriptor.ManagedRuntimeIdentitySha256 is null ||
            descriptor.PrivateNativeLibraries is null)
        {
            throw new InvalidDataException(
                "Android payload manifest does not define runtime identity or deployment policy metadata.");
        }
        var runtimeBackend = AndroidPayloadContract.ParseManagedRuntimeBackend(
            descriptor.ManagedRuntimeBackend);
        if (!IsSha256(descriptor.ManagedRuntimeIdentitySha256))
            throw new InvalidDataException(
                "Android payload manifest contains an invalid managed runtime identity hash.");
        if (descriptor.CoreClrCryptoDexSha256 is not null &&
            !IsSha256(descriptor.CoreClrCryptoDexSha256))
        {
            throw new InvalidDataException(
                "Android payload manifest contains an invalid crypto helper dex hash.");
        }
        if (descriptor.RuntimeRid is not null && descriptor.RuntimeRid is not ("android-arm64" or "linux-bionic-arm64"))
            throw new InvalidDataException("Unsupported runtime RID.");
        if (descriptor.RuntimeRid == "linux-bionic-arm64" &&
            (runtimeBackend != ManagedRuntimeBackend.CoreClr ||
             descriptor.CoreClrCryptoDexSha256 is not null))
            throw new InvalidDataException("Invalid Bionic cryptography metadata.");
        if (descriptor.ExperimentalRuntimeRid is not null &&
            (descriptor.ExperimentalRuntimeRid != "linux-bionic-arm64" ||
             runtimeBackend != ManagedRuntimeBackend.CoreClr ||
             descriptor.CoreClrCryptoDexSha256 is not null))
            throw new InvalidDataException("Invalid experimental runtime payload metadata.");
        if (runtimeBackend == ManagedRuntimeBackend.MonoVmSgen &&
            descriptor.CoreClrCryptoDexSha256 is not null)
        {
            throw new InvalidDataException(
                "Android MonoVM/SGen payload manifest declares a CoreCLR crypto helper dex.");
        }
        if (descriptor.PrivateNativeLibraries.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                name.Contains('/') ||
                name.Contains('\\') ||
                !name.EndsWith(".so", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Android payload manifest contains an invalid private native library name.");
        }
        return descriptor;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void ValidateUniqueEntries(ZipArchive archive)
    {
        var duplicate = archive.Entries
            .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException($"APK contains duplicate ZIP entry '{duplicate.Key}'.");
    }

    private static void ValidateRuntimeEntries(ZipArchive archive)
    {
        var unsupported = archive.Entries.FirstOrDefault(entry =>
            !string.IsNullOrEmpty(entry.Name) &&
            entry.FullName.StartsWith(
                $"{AndroidPayloadContract.PayloadRoot}/runtime/",
                StringComparison.Ordinal) &&
            !AndroidPayloadContract.IsRuntimeDomainPath(entry.FullName));
        if (unsupported is not null)
        {
            throw new InvalidDataException(
                $"Android runtime entry '{unsupported.FullName}' is outside a supported update domain.");
        }
    }

    private static void ValidateDeploymentEntries(ZipArchive archive)
    {
        const string prefix = AndroidPayloadContract.DeploymentRoot + "/";
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
            var publicEntry = $"{GamePackageLayout.Arm64LibraryRoot}/{library}";
            if (archive.GetEntry(publicEntry) is not null)
            {
                throw new InvalidDataException(
                    $"The input contains '{publicEntry}', which conflicts with LemonLoader's " +
                    "private .NET native dependency. This input cannot be patched without " +
                    "isolating that dependency first.");
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

    private static void RejectExistingLoaderPayload(string gameRoot)
    {
        foreach (var entryName in new[]
                 {
                     "assets/lemonloader_asset_hash.txt",
                     "assets/dotnet",
                     "assets/MelonLoader",
                     "assets/LemonLoader"
                 })
        {
            var path = GamePackageLayout.FilePath(gameRoot, entryName);
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            throw new InvalidDataException(
                $"The input directory already contains a loader payload at '{entryName}'. " +
                "Patch an original game directory instead.");
        }
    }

    private static void AddTree(ZipArchive archive, string root, string prefix)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            AddFile(
                archive,
                file,
                $"{prefix}/{Path.GetRelativePath(root, file).Replace('\\', '/')}");
        }
    }

    private static void AddNativeTree(ZipArchive archive, string root)
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = $"lib/{Path.GetRelativePath(root, file).Replace('\\', '/')}";
            AddFile(
                archive,
                file,
                name,
                replaceExisting: name == GamePackageLayout.MainLibrary);
        }
    }

    private static string? AddCoreClrCryptoDex(
        ZipArchive archive,
        PayloadSource source,
        PayloadDescriptor descriptor)
    {
        var runtimeBackend = AndroidPayloadContract.ParseManagedRuntimeBackend(
            descriptor.ManagedRuntimeBackend);
        var isBionic = descriptor.RuntimeRid == "linux-bionic-arm64" ||
            descriptor.ExperimentalRuntimeRid == "linux-bionic-arm64";
        if (runtimeBackend != ManagedRuntimeBackend.CoreClr || isBionic)
            return null;

        var cryptoDex = GamePackageLayout.FilePath(
            source.ReleaseRoot,
            AndroidPayloadContract.CoreClrCryptoDexReleasePath);
        RequireFile(cryptoDex, "Android CoreCLR crypto helper dex");
        if (!source.VerifiedReleaseFiles.TryGetValue(
                AndroidPayloadContract.CoreClrCryptoDexReleasePath,
                out var verifiedCryptoDex))
        {
            throw new InvalidDataException(
                "The validated Android CoreCLR Release has no crypto helper dex digest.");
        }
        if (descriptor.CoreClrCryptoDexSha256 is not null &&
            !string.Equals(
                verifiedCryptoDex.Hash,
                descriptor.CoreClrCryptoDexSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Android CoreCLR crypto helper dex does not match payload.json.");
        }

        var dexIndices = archive.Entries
            .Where(entry => !entry.FullName.Contains('/'))
            .Select(entry => ParseDexIndex(entry.FullName))
            .Where(index => index is not null)
            .Select(index => index!.Value)
            .ToArray();
        if (!dexIndices.Contains(1))
        {
            throw new InvalidDataException(
                "The input Android package has no primary classes.dex for the CoreCLR crypto bridge.");
        }
        var nextIndex = checked(dexIndices.Max() + 1);
        AddFile(archive, cryptoDex, $"classes{nextIndex}.dex");
        return verifiedCryptoDex.Hash;
    }

    private static int? ParseDexIndex(string entryName)
    {
        if (entryName == "classes.dex")
            return 1;
        if (!entryName.StartsWith("classes", StringComparison.Ordinal) ||
            !entryName.EndsWith(".dex", StringComparison.Ordinal))
        {
            return null;
        }
        var value = entryName["classes".Length..(entryName.Length - ".dex".Length)];
        return int.TryParse(value, out var index) && index >= 2 ? index : null;
    }

    private static void AddDeploymentRoot(ZipArchive archive, string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Deployment directory was not found at '{sourcePath}'.",
                sourcePath);
        var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            throw new InvalidDataException(
                $"Deployment directory '{sourcePath}' contains no files.");
        }
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourcePath, file).Replace('\\', '/');
            ValidateDeploymentRootPath(relativePath);
            AddFile(archive, file, $"{AndroidPayloadContract.DeploymentRoot}/{relativePath}");
        }
    }

    private static void ValidateDeploymentRootPath(string relativePath)
    {
        var topLevel = relativePath.Split('/', 2)[0];
        foreach (var standardDirectory in new[] { "Mods", "Plugins", "UserLibs", "UserData" })
        {
            if (string.Equals(
                    topLevel,
                    standardDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(topLevel, standardDirectory, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Deployment directory '{topLevel}' must use Android casing " +
                    $"'{standardDirectory}'.");
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
        {
            throw new InvalidDataException(
                $"Refusing to overwrite existing package entry '{name}'. " +
                $"Only '{GamePackageLayout.MainLibrary}' may be replaced.");
        }
        existing?.Delete();
        archive.CreateEntryFromFile(
            path,
            name,
            name.StartsWith("lib/", StringComparison.Ordinal)
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal);
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{description} was not found at '{path}'.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record PayloadDescriptor(
        int FormatVersion,
        string ManagedRuntimeBackend,
        string ManagedRuntimeIdentitySha256,
        string? CoreClrCryptoDexSha256,
        string LoaderSha256,
        string DotnetSha256,
        string InteropSha256,
        string DeploymentSha256,
        string DeploymentProfile,
        string DeploymentRevisionSha256,
        IReadOnlyList<DeploymentFileDescriptor> DeploymentFiles,
        IReadOnlyList<string> PrivateNativeLibraries,
        string? ExperimentalRuntimeRid = null,
        string? RuntimeRid = null);

    internal sealed record DeploymentFileDescriptor(
        string Path,
        long Size,
        string Sha256,
        string Policy);
}
