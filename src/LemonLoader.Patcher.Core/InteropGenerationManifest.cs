using System.Text.Json;

namespace LemonLoader.Patcher.Core;

internal static class InteropGenerationManifest
{
    public const string FileName = "interop-manifest.json";

    public static void Write(
        string outputDirectory,
        string unityVersion,
        UnityDependenciesResolution unityDependencies,
        string cpp2IlVersion,
        InteropGeneratorTool il2CppInterop)
    {
        var assemblies = Directory.GetFiles(outputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new { name = Path.GetFileName(path), size = new FileInfo(path).Length })
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
                unstrippingEnabled = true
            },
            tools = new
            {
                cpp2IlVersion,
                il2CppInteropVersion = il2CppInterop.Version,
                il2CppInteropSource = il2CppInterop.Source,
                xrefCache = false
            },
            assemblies
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, FileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

}
