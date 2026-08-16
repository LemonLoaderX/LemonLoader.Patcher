using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record UnityDependencySource(string Name, string UrlTemplate)
{
    public Uri GetUri(string version) => new(string.Format(UrlTemplate, version));
}

internal sealed record UnityDependenciesResolution(
    string DirectoryPath,
    string RequestedVersion,
    string PackageVersion,
    string Source,
    string? DownloadUrl,
    string? ArchiveSha256,
    int AssemblyCount,
    string ContentSha256);

internal static class UnityDependenciesResolver
{
    internal const string ManifestFileName = "lemonloader-unity-dependencies.json";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static readonly IReadOnlyList<UnityDependencySource> DefaultSources =
    [
        new(
            "MelonLoader.UnityDependencies",
            "https://github.com/LavaGang/MelonLoader.UnityDependencies/releases/download/{0}/Managed.zip"),
        new(
            "BepInEx.UnityData",
            "https://unity.bepinex.dev/libraries/{0}.zip")
    ];

    public static async Task<UnityDependenciesResolution> ResolveAsync(
        string cacheRoot,
        string unityVersion,
        CancellationToken cancellationToken = default)
        => await ResolveAsync(cacheRoot, unityVersion, null, cancellationToken);

    public static async Task<UnityDependenciesResolution> ResolveAsync(
        string cacheRoot,
        string unityVersion,
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = DownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LemonLoader.Patcher", "1.0"));
        return await ResolveAsync(
            cacheRoot,
            unityVersion,
            client,
            DefaultSources,
            progress,
            cancellationToken);
    }

    internal static async Task<UnityDependenciesResolution> ResolveAsync(
        string cacheRoot,
        string unityVersion,
        HttpClient client,
        IReadOnlyList<UnityDependencySource> sources,
        CancellationToken cancellationToken)
        => await ResolveAsync(
            cacheRoot,
            unityVersion,
            client,
            sources,
            null,
            cancellationToken);

    internal static async Task<UnityDependenciesResolution> ResolveAsync(
        string cacheRoot,
        string unityVersion,
        HttpClient client,
        IReadOnlyList<UnityDependencySource> sources,
        IProgress<PatcherMessage>? progress,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
            throw new ArgumentException("At least one Unity dependency source is required.", nameof(sources));

        var requestedVersion = unityVersion.Trim();
        var packageVersion = NormalizeVersion(requestedVersion);
        var root = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(root);
        var destination = GetOwnedPath(root, packageVersion);

        if (TryReadCachedResolution(
                destination,
                requestedVersion,
                packageVersion,
                out var cachedResolution))
        {
            progress?.Report(new(
                PatcherMessageKind.Stage,
                $"Using cached Unity dependencies: {destination}"));
            return cachedResolution;
        }

        if (Directory.Exists(destination))
            Directory.Delete(destination, true);

        var failures = new List<Exception>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceUri = source.GetUri(packageVersion);
            var archivePath = GetOwnedPath(
                root,
                $"Managed-{packageVersion}-{SanitizeFileName(source.Name)}.zip");
            if (File.Exists(archivePath))
            {
                try
                {
                    var cachedArchiveResolution = ExtractAndPublish(
                        archivePath,
                        destination,
                        requestedVersion,
                        packageVersion,
                        source);
                    progress?.Report(new(
                        PatcherMessageKind.Stage,
                        $"Restored Unity dependencies {packageVersion} from cache"));
                    return cachedArchiveResolution;
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException)
                {
                    failures.Add(new InvalidDataException(
                        $"Cached Unity dependency archive '{archivePath}' is invalid and will be replaced.",
                        exception));
                    File.Delete(archivePath);
                }
            }

            var temporaryArchive = GetOwnedPath(
                root,
                $".Managed-{packageVersion}-{Guid.NewGuid():N}.download");
            try
            {
                progress?.Report(new(
                    PatcherMessageKind.Stage,
                    $"Downloading Unity dependencies {packageVersion} from {source.Name}"));
                using var response = await client.GetAsync(
                    sourceUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    failures.Add(new HttpRequestException(
                        $"{source.Name} does not contain Unity {packageVersion}.",
                        null,
                        response.StatusCode));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength == 0)
                    throw new InvalidDataException($"{source.Name} returned an empty Unity dependency archive.");

                await using (var output = new FileStream(
                                 temporaryArchive,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await response.Content.CopyToAsync(output, cancellationToken);
                }

                var resolution = ExtractAndPublish(
                    temporaryArchive,
                    destination,
                    requestedVersion,
                    packageVersion,
                    source);
                File.Move(temporaryArchive, archivePath, true);
                progress?.Report(new(
                    PatcherMessageKind.Stage,
                    $"Restored {resolution.AssemblyCount} Unity assemblies for {packageVersion}"));
                return resolution;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                failures.Add(new InvalidOperationException(
                    $"Could not use Unity dependency source {source.Name} ({sourceUri}).",
                    exception));
            }
            finally
            {
                if (File.Exists(temporaryArchive))
                    File.Delete(temporaryArchive);
            }
        }

        throw new AggregateException(
            $"Could not restore Unity base libraries for '{requestedVersion}' (package {packageVersion}).",
            failures);
    }

    public static UnityDependenciesResolution UseLocal(string directoryPath, string unityVersion)
    {
        var directory = Path.GetFullPath(directoryPath);
        var packageVersion = NormalizeVersion(unityVersion);
        if (TryReadCachedResolution(
                directory,
                unityVersion,
                packageVersion,
                out var recordedResolution))
        {
            return recordedResolution;
        }
        var inspection = Inspect(directory);
        return new(
            directory,
            unityVersion,
            packageVersion,
            "local",
            null,
            null,
            inspection.AssemblyCount,
            inspection.ContentSha256);
    }

    public static void Publish(UnityDependenciesResolution resolution, string destinationPath) =>
        DirectoryPublisher.Replace(resolution.DirectoryPath, destinationPath);

    public static int Validate(string directoryPath) => Inspect(directoryPath).AssemblyCount;

    private static UnityDependenciesInspection Inspect(string directoryPath)
    {
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
            throw new InvalidDataException($"Unity base library directory was not found at '{directory}'.");

        var assemblyPaths = Directory.GetFiles(directory, "UnityEngine*.dll", SearchOption.TopDirectoryOnly);
        if (!assemblyPaths.Any(path =>
                string.Equals(Path.GetFileName(path), "UnityEngine.dll", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Unity base libraries at '{directory}' do not contain UnityEngine.dll.");
        }

        var fingerprints = new List<string>(assemblyPaths.Length);
        foreach (var assemblyPath in assemblyPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                _ = AssemblyName.GetAssemblyName(assemblyPath);
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
            {
                throw new InvalidDataException(
                    $"Unity base library '{Path.GetFileName(assemblyPath)}' is not a valid managed assembly.",
                    exception);
            }
            using var input = File.OpenRead(assemblyPath);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            fingerprints.Add($"{Path.GetFileName(assemblyPath)}|{input.Length}|{hash}");
        }

        var content = string.Join('\n', fingerprints);
        var contentHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new(assemblyPaths.Length, contentHash);
    }

    internal static string NormalizeVersion(string unityVersion)
    {
        var match = Regex.Match(
            unityVersion.Trim(),
            @"^(?<version>\d+\.\d+\.\d+)(?:[abfp]\d+)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new InvalidDataException($"Unsupported Unity version '{unityVersion}'.");
        return match.Groups["version"].Value;
    }

    private static UnityDependenciesResolution ExtractAndPublish(
        string archivePath,
        string destination,
        string requestedVersion,
        string packageVersion,
        UnityDependencySource source)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var staging = GetOwnedPath(parent, $".{packageVersion}.staging-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);
            var inspection = Inspect(staging);
            using var archive = File.OpenRead(archivePath);
            var archiveHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var resolution = new UnityDependenciesResolution(
                destination,
                requestedVersion,
                packageVersion,
                source.Name,
                source.GetUri(packageVersion).AbsoluteUri,
                archiveHash,
                inspection.AssemblyCount,
                inspection.ContentSha256);
            WriteManifest(staging, resolution with { DirectoryPath = staging });

            if (Directory.Exists(destination))
            {
                if (TryReadCachedResolution(
                        destination,
                        requestedVersion,
                        packageVersion,
                        out var concurrentResolution))
                {
                    return concurrentResolution;
                }
                Directory.Delete(destination, true);
            }
            Directory.Move(staging, destination);
            return resolution;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Could not extract Unity dependency archive '{archivePath}'.",
                exception);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static bool TryReadCachedResolution(
        string destination,
        string requestedVersion,
        string packageVersion,
        out UnityDependenciesResolution resolution)
    {
        resolution = null!;
        if (!Directory.Exists(destination))
            return false;
        try
        {
            var inspection = Inspect(destination);
            var manifestPath = Path.Combine(destination, ManifestFileName);
            if (!File.Exists(manifestPath))
                return false;
            var manifest = JsonSerializer.Deserialize<UnityDependenciesCacheManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            if (manifest is null || manifest.FormatVersion != 2 ||
                !string.Equals(manifest.PackageVersion, packageVersion, StringComparison.Ordinal) ||
                manifest.AssemblyCount != inspection.AssemblyCount ||
                !string.Equals(
                    manifest.ContentSha256,
                    inspection.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolution = new(
                destination,
                requestedVersion,
                packageVersion,
                manifest.Source,
                manifest.DownloadUrl,
                manifest.ArchiveSha256,
                inspection.AssemblyCount,
                inspection.ContentSha256);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    private static void WriteManifest(string directory, UnityDependenciesResolution resolution)
    {
        var manifest = new UnityDependenciesCacheManifest(
            2,
            resolution.RequestedVersion,
            resolution.PackageVersion,
            resolution.Source,
            resolution.DownloadUrl,
            resolution.ArchiveSha256,
            resolution.AssemblyCount,
            resolution.ContentSha256);
        File.WriteAllText(
            Path.Combine(directory, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static string GetOwnedPath(string root, string name)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, name));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException($"Resolved cache path '{path}' escapes '{fullRoot}'.");
        return path;
    }

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private sealed record UnityDependenciesCacheManifest(
        int FormatVersion,
        string RequestedVersion,
        string PackageVersion,
        string Source,
        string? DownloadUrl,
        string? ArchiveSha256,
        int AssemblyCount,
        string ContentSha256);

    private sealed record UnityDependenciesInspection(
        int AssemblyCount,
        string ContentSha256);
}
