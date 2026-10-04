namespace LemonLoader.Patcher.Core;

internal static class ToolCachePaths
{
    internal static string Root => Path.Combine(AppContext.BaseDirectory, ".tools");
    internal static string UnityDependencies => Path.Combine(Root, "UnityDependencies");
}
