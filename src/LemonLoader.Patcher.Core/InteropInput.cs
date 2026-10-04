namespace LemonLoader.Patcher.Core;

internal static class InteropInput
{
    internal static string[] Assemblies(string directory)
    {
        if (!Directory.Exists(directory))
            throw new ArgumentException($"Interop directory was not found at '{directory}'.");
        PathSafety.RejectLinks(directory);
        var files = Directory.EnumerateFiles(directory).Where(file => Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0)
            throw new ArgumentException("Interop directory must contain generated DLLs at its root.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            PathSafety.RejectLinks(file);
            if (!names.Add(Path.GetFileName(file)) || new FileInfo(file).Length == 0)
                throw new ArgumentException("Interop DLL names must be unique and files must be nonempty.");
        }
        return files;
    }

    internal static void Copy(string source, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        foreach (var dll in Assemblies(source))
            DirectoryPublisher.CopyFile(dll, Path.Combine(output, Path.GetFileName(dll)), cancellationToken);
    }
}
