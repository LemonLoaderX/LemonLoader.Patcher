using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record InteropGeneratorTool(
    string Path,
    string Version,
    string Source,
    string Sha256,
    string ContentSha256)
{
    public static InteropGeneratorTool FromOverride(string path) =>
        Describe(path, "override");

    public static InteropGeneratorTool FromBundledFork(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var provenancePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(fullPath)!,
            BundledInteropGeneratorTool.ProvenanceFileName);
        if (!File.Exists(provenancePath))
            throw new FileNotFoundException(
                "The bundled Il2CppInterop generator provenance is missing.",
                provenancePath);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(provenancePath));
            var root = document.RootElement;
            var formatVersion = root.GetProperty("formatVersion").GetInt32();
            var revision = root.GetProperty("revision").GetString();
            if (formatVersion != 1 ||
                !string.Equals(revision, BundledInteropGeneratorTool.Revision, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The bundled Il2CppInterop generator provenance does not match revision " +
                    $"'{BundledInteropGeneratorTool.Revision}'.");
            }
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"The bundled Il2CppInterop generator provenance '{provenancePath}' is invalid.",
                exception);
        }

        return Describe(fullPath, "bundled-fork");
    }

    private static InteropGeneratorTool Describe(
        string path,
        string source)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Il2CppInterop CLI was not found.", fullPath);
        if (!string.Equals(System.IO.Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Il2CppInterop CLI must be a managed .dll file.");

        AssemblyName assemblyName;
        try
        {
            assemblyName = AssemblyName.GetAssemblyName(fullPath);
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException(
                $"The Il2CppInterop CLI '{fullPath}' is not a managed assembly.",
                exception);
        }

        var version = assemblyName.Version?.ToString();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException($"The Il2CppInterop CLI '{fullPath}' has no assembly version.");

        var hash = HashFile(fullPath);
        var contentHash = ComputeContentHash(System.IO.Path.GetDirectoryName(fullPath)!);
        return new(fullPath, version, source, hash, contentHash);
    }

    private static string ComputeContentHash(string directory)
    {
        var files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                string.Equals(System.IO.Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    System.IO.Path.GetFileName(path),
                    BundledInteropGeneratorTool.ProvenanceFileName,
                    StringComparison.Ordinal))
            .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        using var contentHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var line = $"{info.Name}|{info.Length}|{HashFile(file)}\n";
            contentHasher.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(contentHasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}

internal static class BundledInteropGeneratorTool
{
    public static readonly string Revision = typeof(BundledInteropGeneratorTool).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "BundledIl2CppInteropRevision")
        .Value ?? throw new InvalidOperationException(
            "The bundled Il2CppInterop revision metadata has no value.");
    public const string ProvenanceFileName = "lemonloader-il2cppinterop.json";
    private const string ToolDirectory = "Il2CppInterop";
    private const string ToolFileName = "Il2CppInterop.CLI.dll";

    public static string FindToolDll()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", ToolDirectory, ToolFileName),
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "Tools", ToolDirectory, ToolFileName)
        };
        var path = candidates
            .Select(System.IO.Path.GetFullPath)
            .FirstOrDefault(File.Exists);
        return path ?? throw new FileNotFoundException(
            "The bundled LemonLoader Il2CppInterop generator is missing. " +
            "Reinstall the complete Patcher release or supply --il2cppinterop-cli explicitly.",
            candidates[0]);
    }
}
