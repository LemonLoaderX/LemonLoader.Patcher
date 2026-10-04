using System.Reflection;
using System.Text.Json;

namespace LemonLoader.Patcher.Core;

internal sealed record InteropGeneratorTool(
    string Path,
    string Version,
    string Source)
{
    public static InteropGeneratorTool FromOverride(string path) => Describe(path, "override");

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

        return new(fullPath, version, source);
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
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", ToolDirectory, ToolFileName);
        return File.Exists(path) ? path : throw new FileNotFoundException(
            "The bundled LemonLoader Il2CppInterop generator is missing. " +
            "Reinstall the complete Patcher release or supply --il2cppinterop-cli explicitly.",
            path);
    }
}
