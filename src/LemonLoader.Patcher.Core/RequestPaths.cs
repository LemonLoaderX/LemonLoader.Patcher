namespace LemonLoader.Patcher.Core;

internal static class RequestPaths
{
    internal static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);

    internal static void RequireFile(string? path, string name)
    {
        if (path is null || !File.Exists(path))
            throw new ArgumentException($"{name} was not found at '{path}'.");
    }

    internal static void ValidateOutputFile(string output, params string?[] inputs)
    {
        PathSafety.RejectLinks(output);
        if (Directory.Exists(output))
            throw new ArgumentException("APK output must be a file.");
        foreach (var input in inputs.OfType<string>().Append(ToolCachePaths.Root)
                     .Append(Path.Combine(AppContext.BaseDirectory, "Tools")))
        {
            PathSafety.RejectLinks(input);
            if (PathSafety.Contains(input, output))
                throw new ArgumentException($"APK output must not overwrite an input or tool: '{input}'.");
        }
    }

    internal static void ValidateExport(string export, params string?[] inputs)
    {
        PathSafety.RejectLinks(export);
        if (PathSafety.Contains(export, AppContext.BaseDirectory))
            throw new ArgumentException("Interop output must not replace the Patcher application directory.");
        var bundledTools = Path.Combine(AppContext.BaseDirectory, "Tools");
        foreach (var input in inputs.OfType<string>().Append(bundledTools))
        {
            PathSafety.RejectLinks(input);
            if (PathSafety.Contains(export, input) ||
                ((input == ToolCachePaths.Root || input == bundledTools || Directory.Exists(input)) && PathSafety.Contains(input, export)))
                throw new ArgumentException("Interop output must not overlap game, dependency, deployment or other input/output paths.");
        }
    }
}
