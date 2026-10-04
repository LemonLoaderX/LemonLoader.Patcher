using System.Text.Json;

namespace LemonLoader.Patcher.Core;

internal static class AndroidPayloadContract
{
    public const int FormatVersion = 9;
    public const string PayloadRoot = "assets/LemonLoader";
    public const string LoaderRoot = $"{PayloadRoot}/runtime/loader";
    public const string DotnetRoot = $"{PayloadRoot}/runtime/dotnet";
    public const string InteropRoot = $"{PayloadRoot}/runtime/interop";
    public const string DeploymentRoot = $"{PayloadRoot}/deployment";
    public const string PayloadManifestPath = $"{PayloadRoot}/payload.json";

    public static string? ReadCryptoDexMode(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("coreClrCryptoDexMode", out var mode) || mode.ValueKind == JsonValueKind.Null)
            return null;
        if (mode.ValueKind != JsonValueKind.String || mode.GetString() != "embedded")
            throw new InvalidDataException("Unsupported Android crypto DEX mode.");
        return "embedded";
    }

    public static bool IsRuntimeDomainPath(string path) =>
        path.StartsWith($"{LoaderRoot}/", StringComparison.Ordinal) ||
        path.StartsWith($"{DotnetRoot}/", StringComparison.Ordinal) ||
        path.StartsWith($"{InteropRoot}/", StringComparison.Ordinal);
}
