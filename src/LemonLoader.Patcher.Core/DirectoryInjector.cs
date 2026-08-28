internal static class DirectoryInjector
{
    public static void Apply(
        string gameRoot,
        string overlayRoot,
        IProgress<PatcherMessage>? progress)
    {
        ValidateTargets(gameRoot, overlayRoot);
        var transactionRoot = Path.Combine(
            gameRoot,
            $".lemonloader-patcher-{Guid.NewGuid():N}");
        var stagedRoot = Path.Combine(transactionRoot, "staged");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var installed = new List<(string Destination, string? Backup)>();
        var createdDirectories = new HashSet<string>(PathComparer);
        var committed = false;
        var operationFailed = false;
        var preserveTransaction = false;
        try
        {
            CopyTree(overlayRoot, stagedRoot);
            var stagedFiles = Directory.GetFiles(stagedRoot, "*", SearchOption.AllDirectories)
                .OrderBy(
                    path => Path.GetRelativePath(stagedRoot, path).Replace('\\', '/'),
                    StringComparer.Ordinal)
                .ToArray();
            foreach (var stagedPath in stagedFiles)
            {
                var relativePath = Path.GetRelativePath(stagedRoot, stagedPath);
                var destination = Path.Combine(gameRoot, relativePath);
                CreateMissingDirectories(
                    Path.GetDirectoryName(destination)!,
                    gameRoot,
                    createdDirectories);
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = Path.Combine(backupRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, true);
                }
                File.Move(stagedPath, destination, true);
                installed.Add((destination, backup));
            }
            committed = true;
        }
        catch (Exception failure)
        {
            operationFailed = true;
            if (TryRollBack(installed, createdDirectories) is { } rollbackFailure)
            {
                preserveTransaction = true;
                throw new AggregateException(
                    "Directory injection failed and rollback was incomplete. " +
                    $"Recovery files were preserved at '{transactionRoot}'.",
                    failure,
                    rollbackFailure);
            }
            throw new IOException(
                "Directory injection failed. Files changed by this operation were restored.",
                failure);
        }
        finally
        {
            try
            {
                if (!preserveTransaction && Directory.Exists(transactionRoot))
                    Directory.Delete(transactionRoot, true);
            }
            catch (Exception cleanupException)
                when ((committed || operationFailed) && progress is not null)
            {
                var outcome = committed
                    ? "succeeded"
                    : "failed and was rolled back";
                progress.Report(new(
                    PatcherMessageKind.Warning,
                    $"Directory injection {outcome}, but transaction directory '{transactionRoot}' " +
                    $"could not be removed: {cleanupException.Message}"));
            }
        }
    }

    private static void ValidateTargets(string gameRoot, string overlayRoot)
    {
        foreach (var sourcePath in Directory.GetFiles(overlayRoot, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.GetRelativePath(overlayRoot, sourcePath).Replace('\\', '/');
            var destination = GamePackageLayout.FilePath(gameRoot, entryName);
            if (Directory.Exists(destination))
            {
                throw new InvalidDataException(
                    $"Directory payload entry '{entryName}' conflicts with an existing directory.");
            }
            if (File.Exists(destination) &&
                !string.Equals(
                    entryName,
                    GamePackageLayout.MainLibrary,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Refusing to overwrite existing directory entry '{entryName}'. " +
                    $"Only '{GamePackageLayout.MainLibrary}' may be replaced.");
            }

            var parent = Path.GetDirectoryName(destination);
            while (parent is not null && !PathEquals(parent, gameRoot))
            {
                if (File.Exists(parent))
                {
                    throw new InvalidDataException(
                        $"Directory payload entry '{entryName}' conflicts with existing file '{parent}'.");
                }
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static Exception? TryRollBack(
        IReadOnlyList<(string Destination, string? Backup)> installed,
        IEnumerable<string> createdDirectories)
    {
        try
        {
            foreach (var item in installed.Reverse())
            {
                if (item.Backup is not null && File.Exists(item.Backup))
                    File.Move(item.Backup, item.Destination, true);
                else if (File.Exists(item.Destination))
                    File.Delete(item.Destination);
            }
            foreach (var directory in createdDirectories.OrderByDescending(path => path.Length))
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            return null;
        }
        catch (Exception rollbackFailure)
        {
            return rollbackFailure;
        }
    }

    private static void CopyTree(string sourceRoot, string destinationRoot)
    {
        foreach (var sourcePath in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination);
        }
    }

    private static void CreateMissingDirectories(
        string directory,
        string stopAt,
        ISet<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var current = directory;
        while (!Directory.Exists(current) && !PathEquals(current, stopAt))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    $"Could not resolve the parent of '{current}'.");
        }
        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            createdDirectories.Add(path);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IEqualityComparer<string> PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
