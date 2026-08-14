using System.Security.Cryptography;
using System.Text.Json;

internal static class InteropGenerationManifest
{
    public const string FileName = "interop-manifest.json";

    public static void Write(
        string outputDirectory,
        string inputDirectory,
        string unityVersion,
        UnityDependenciesResolution unityDependencies,
        string cpp2IlPath,
        string cpp2IlVersion,
        string il2CppInteropVersion)
    {
        var assemblies = Directory.GetFiles(outputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => DescribeFile(path, Path.GetFileName(path)))
            .ToArray();
        if (assemblies.Length == 0)
            throw new InvalidDataException("Il2CppInterop did not generate any assemblies.");

        var manifest = new
        {
            formatVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            unityVersion,
            unityDependencies = new
            {
                packageVersion = unityDependencies.PackageVersion,
                source = unityDependencies.Source,
                downloadUrl = unityDependencies.DownloadUrl,
                archiveSha256 = unityDependencies.ArchiveSha256,
                assemblyCount = unityDependencies.AssemblyCount,
                contentSha256 = unityDependencies.ContentSha256,
                unstrippingEnabled = true
            },
            inputs = new[]
            {
                DescribeFile(Path.Combine(inputDirectory, "libil2cpp.so"), "libil2cpp.so"),
                DescribeFile(Path.Combine(inputDirectory, "global-metadata.dat"), "global-metadata.dat")
            },
            tools = new
            {
                cpp2IlVersion,
                cpp2IlSha256 = HashFile(cpp2IlPath),
                il2CppInteropVersion,
                xrefCache = false
            },
            assemblies
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, FileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object DescribeFile(string path, string name)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException($"Interop manifest input '{name}' was not found.", path);
        return new { name, size = file.Length, sha256 = HashFile(path) };
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
