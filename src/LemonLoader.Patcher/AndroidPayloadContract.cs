using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class AndroidPayloadContract
{
    public const int FormatVersion = 5;
    public const string PayloadRoot = "assets/LemonLoader";
    public const string LoaderRoot = $"{PayloadRoot}/runtime/loader";
    public const string DotnetRoot = $"{PayloadRoot}/runtime/dotnet";
    public const string InteropRoot = $"{PayloadRoot}/runtime/interop";
    public const string DeploymentRoot = $"{PayloadRoot}/deployment";
    public const string PayloadManifestPath = $"{PayloadRoot}/payload.json";

    public static string ComputeTreeHash(ZipArchive archive, string scope)
    {
        var scopePrefix = $"{PayloadRoot}/{scope}/";
        var lines = new List<string>
        {
            $"layout-version={FormatVersion}",
            $"scope={scope}"
        };
        foreach (var entry in archive.Entries
                     .Where(entry =>
                         !string.IsNullOrEmpty(entry.Name) &&
                         entry.FullName.StartsWith(scopePrefix, StringComparison.Ordinal))
                     .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
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
            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            lines.Add($"{entry.FullName[(PayloadRoot.Length + 1)..]}|{length}|{hash}");
        }
        return HashLines(lines);
    }

    public static string ComputeTreeHash(string releaseRoot, string scope)
    {
        var payloadRoot = Path.Combine(
            releaseRoot,
            PayloadRoot.Replace('/', Path.DirectorySeparatorChar));
        var scopeRoot = Path.Combine(
            payloadRoot,
            scope.Replace('/', Path.DirectorySeparatorChar));
        var files = Directory.Exists(scopeRoot)
            ? Directory.EnumerateFiles(scopeRoot, "*", SearchOption.AllDirectories)
            : [];
        var lines = new List<string>
        {
            $"layout-version={FormatVersion}",
            $"scope={scope}"
        };
        foreach (var path in files.OrderBy(
                     path => Path.GetRelativePath(payloadRoot, path).Replace('\\', '/'),
                     StringComparer.Ordinal))
        {
            using var input = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            var relativePath = Path.GetRelativePath(payloadRoot, path).Replace('\\', '/');
            lines.Add($"{relativePath}|{input.Length}|{hash}");
        }
        return HashLines(lines);
    }

    public static bool IsForbiddenReleasePath(string path) =>
        path.StartsWith($"{LoaderRoot}/Documentation/", StringComparison.Ordinal);

    private static string HashLines(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
}
