using System.IO.Compression;
using System.Security.Cryptography;

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
        var suffix = ResolvePlatformSuffix();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"Cpp2IL-{Version}-{suffix}");
        if (File.Exists(path))
        {
            if (string.Equals(
                    await ComputeHashAsync(path, cancellationToken),
                    Hashes[suffix],
                    StringComparison.Ordinal))
            {
                return path;
            }
            File.Delete(path);
        }

        var temporaryPath = Path.Combine(root, $".Cpp2IL-{Guid.NewGuid():N}.download");
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
            if (!string.Equals(
                    await ComputeHashAsync(temporaryPath, cancellationToken),
                    Hashes[suffix],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Downloaded Cpp2IL SHA-256 does not match the pinned tool.");
            }
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
    }

    private static string ResolvePlatformSuffix()
    {
        if (OperatingSystem.IsWindows())
            return "Windows.exe";
        var arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                    System.Runtime.InteropServices.Architecture.Arm64;
        if (OperatingSystem.IsLinux())
            return arm64 ? "Linux-ARM64" : "Linux";
        return arm64 ? "OSX-ARM64" : "OSX";
    }

    private static async Task<string> ComputeHashAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(filePath);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken))
            .ToLowerInvariant();
    }
}

internal static class ReleaseResolver
{
    private const string ReleaseFileName = "LemonLoader-Android-arm64.zip";
    internal const string LatestUrl =
        "https://github.com/anosu/LemonLoader/releases/latest/download/LemonLoader-Android-arm64.zip";

    public static async Task<string> ResolveLatestAsync(
        string root,
        IProgress<PatcherMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
            {
                throw new InvalidDataException(
                    "Downloaded LemonLoader Release is not a valid Release archive.");
            }
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
                $"({status}). Choose a local Release archive with --release or try again " +
                "when the Release repository is available.",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool IsReadableRelease(string archivePath)
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
