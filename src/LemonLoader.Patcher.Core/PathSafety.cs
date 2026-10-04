namespace LemonLoader.Patcher.Core;

internal static class PathSafety
{
    public static bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." ||
            (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Refusing linked path '{current}'.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
