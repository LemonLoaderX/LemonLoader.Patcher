namespace LemonLoader.Patcher.Core;

internal static class DirectoryPublisher
{
    public static void Replace(string sourcePath, string destinationPath,
        IProgress<PatcherMessage>? progress = null, CancellationToken cancellationToken = default,
        Action<string>? deleteBackup = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source directory was not found at '{source}'.");
        if (IsSameOrChild(destination, source) || IsSameOrChild(source, destination))
            throw new InvalidOperationException("Source and destination directories must not contain one another.");
        PathSafety.RejectLinks(destination);

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not resolve the parent of '{destination}'.");
        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(destination);
        var staging = Path.Combine(parent, $".{name}.staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".{name}.backup-{Guid.NewGuid():N}");
        var movedExisting = false;
        try
        {
            CopyDirectory(source, staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedExisting = true;
            }

            Directory.Move(staging, destination);
        }
        catch (Exception exception)
        {
            if (movedExisting && Directory.Exists(backup))
                Directory.Move(backup, destination);
            if (exception is OperationCanceledException) throw;
            throw new IOException(
                $"Could not publish directory '{destination}'. The previous directory was restored.",
                exception);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
        // Publication is committed. A partly deleted backup cannot be rolled back.
        if (movedExisting)
        {
            try
            {
                if (deleteBackup is not null) deleteBackup(backup);
                else Directory.Delete(backup, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new(PatcherMessageKind.Warning,
                    $"Published '{destination}', but could not remove backup '{backup}': {exception.Message}"));
            }
        }
    }

    public static void ReplaceFile(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Publish source was not found.", source);
        if (PathEquals(source, destination))
            throw new InvalidOperationException("Publish source and destination must be different files.");
        PathSafety.RejectLinks(destination);

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not resolve the parent of '{destination}'.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.staging-{Guid.NewGuid():N}");
        try
        {
            CopyFile(source, staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    internal static void CopyFile(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = File.OpenRead(source);
        using var output = File.Create(destination);
        CopyStream(input, output, cancellationToken);
    }

    internal static void CopyStream(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            PathSafety.RejectLinks(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target, cancellationToken);
            else CopyFile(entry, target, cancellationToken);
        }
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
