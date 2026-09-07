using System.Text.Json;

/// <summary>Names and artifact identities for selectable CoreCLR distributions.</summary>
public static class RuntimeVariants
{
    /// <summary>The default download variant.</summary>
    public const string Default = "android";

    /// <summary>Validates a variant and resolves an omitted choice to the default.</summary>
    public static string Normalize(string? value) => value switch
    {
        null or "" => Default,
        "android" or "bionic" => value,
        _ => throw new ArgumentException("Runtime must be android or bionic.")
    };

    /// <summary>Returns the variant-specific release and cache filename.</summary>
    public static string ArchiveName(string? value) =>
        $"LemonLoader-runtime-{Normalize(value)}-arm64.zip";

    internal static void ValidateManifest(JsonElement manifest, string variant)
    {
        var expected = Normalize(variant) == "bionic" ? "linux-bionic-arm64" : "android-arm64";
        var actual = manifest.TryGetProperty("runtimeRid", out var rid) ? rid.GetString() :
            manifest.TryGetProperty("experimentalRuntimeRid", out var oldRid) ? oldRid.GetString() : "android-arm64";
        if (actual != expected)
            throw new InvalidDataException($"Selected runtime '{variant}' requires RID '{expected}', but the Release contains '{actual}'.");
    }
}
