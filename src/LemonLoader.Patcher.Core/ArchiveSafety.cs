using System.IO.Compression;

namespace LemonLoader.Patcher.Core;

internal static class ArchiveSafety
{
    public static void Validate(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            ValidatePath(entry.FullName);
            if (!names.Add(entry.FullName))
                throw new InvalidDataException($"Archive contains duplicate ZIP entry '{entry.FullName}'.");
        }
    }

    public static void ValidatePath(string name)
    {
        var path = name.EndsWith('/') ? name[..^1] : name;
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Archive contains unsafe ZIP entry '{name}'.");
    }

    public static void Extract(ZipArchive archive, string root, CancellationToken cancellationToken)
    {
        Validate(archive);
        Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew);
                DirectoryPublisher.CopyStream(input, output, cancellationToken);
            }
        }
    }
}
