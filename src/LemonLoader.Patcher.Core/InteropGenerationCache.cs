using System.Security.Cryptography;
using System.Text.Json;

internal static class InteropGenerationCache
{
    // Change this when generator arguments or output post-processing changes.
    private const int GenerationVersion = 1;

    internal static string Key(string inputRoot, string unityVersion,
        UnityDependenciesResolution unity, string cpp2Il, InteropGeneratorTool interop) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            GenerationVersion, unityVersion,
            binary = HashFile(Path.Combine(inputRoot, "libil2cpp.so")),
            metadata = HashFile(Path.Combine(inputRoot, "global-metadata.dat")),
            unityContent = unity.ContentSha256,
            cpp2Il = HashFile(cpp2Il),
            interopContent = interop.ContentSha256,
            interop.Source
        }))).ToLowerInvariant();

    internal static bool TryRestore(string cache, string key, string output,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cache))
            return false;
        PathSafety.RejectLinks(cache);
        try
        {
            var manifestPath = Path.Combine(cache, InteropGenerationManifest.FileName);
            PathSafety.RejectLinks(manifestPath);
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            if (root.GetProperty("formatVersion").GetInt32() != 1 ||
                !root.TryGetProperty("cacheKey", out var identity) || identity.GetString() != key)
                return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in root.GetProperty("assemblies").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = file.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') ||
                    !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(':') || !names.Add(name))
                    return false;
                var path = Path.Combine(cache, name);
                PathSafety.RejectLinks(path);
                if (!File.Exists(path) || new FileInfo(path).Length != file.GetProperty("size").GetInt64() ||
                    HashFile(path) != file.GetProperty("sha256").GetString())
                    return false;
            }
            if (names.Count == 0 || !names.SetEquals(Directory.EnumerateFiles(cache, "*.dll").Select(Path.GetFileName)))
                return false;
            foreach (var name in names.Append(InteropGenerationManifest.FileName))
                DirectoryPublisher.CopyFile(Path.Combine(cache, name), Path.Combine(output, name), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            foreach (var path in Directory.EnumerateFiles(output))
                File.Delete(path);
            return false;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
