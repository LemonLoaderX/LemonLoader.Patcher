internal static class DirectoryPublisher
{
    public static void Replace(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source directory was not found at '{source}'.");
        if (IsSameOrChild(destination, source) || IsSameOrChild(source, destination))
            throw new InvalidOperationException("Source and destination directories must not contain one another.");

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not resolve the parent of '{destination}'.");
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(destination);
        var staging = Path.Combine(parent, $".{name}.staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".{name}.backup-{Guid.NewGuid():N}");
        CopyDirectory(source, staging);

        var movedExisting = false;
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedExisting = true;
            }

            Directory.Move(staging, destination);
            if (movedExisting)
                Directory.Delete(backup, true);
        }
        catch (Exception exception)
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, true);
            if (movedExisting && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw new IOException(
                $"Could not publish directory '{destination}'. The previous directory was restored.",
                exception);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    public static void ReplaceFile(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Publish source was not found.", source);
        if (PathEquals(source, destination))
            throw new InvalidOperationException("Publish source and destination must be different files.");

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not resolve the parent of '{destination}'.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.staging-{Guid.NewGuid():N}");
        try
        {
            File.Copy(source, staging, true);
            File.Move(staging, destination, true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
    }

    private static bool IsSameOrChild(string candidatePath, string parentPath)
    {
        if (PathEquals(candidatePath, parentPath))
            return true;
        var parentWithSeparator = parentPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(parentWithSeparator, PathComparison);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
