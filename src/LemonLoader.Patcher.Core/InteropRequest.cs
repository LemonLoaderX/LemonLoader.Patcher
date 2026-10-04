namespace LemonLoader.Patcher.Core;

public sealed record InteropRequest
{
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? GameAssemblyPath { get; init; }
    public string? MetadataPath { get; init; }
    public string? UnityVersion { get; init; }
    public string? UnityLibrariesPath { get; init; }
    public string? Cpp2IlPath { get; init; }
    public string? Il2CppInteropCliPath { get; init; }
    internal PatchInputKind InputKind { get; init; }

    public InteropRequest NormalizeAndValidate()
    {
        var request = this with
        {
            InputPath = RequestPaths.Optional(InputPath), OutputPath = RequestPaths.Optional(OutputPath),
            GameAssemblyPath = RequestPaths.Optional(GameAssemblyPath), MetadataPath = RequestPaths.Optional(MetadataPath),
            UnityLibrariesPath = RequestPaths.Optional(UnityLibrariesPath), Cpp2IlPath = RequestPaths.Optional(Cpp2IlPath),
            Il2CppInteropCliPath = RequestPaths.Optional(Il2CppInteropCliPath),
            UnityVersion = string.IsNullOrWhiteSpace(UnityVersion) ? null : UnityVersion.Trim()
        };
        if (request.InputPath is { } input)
        {
            if (!File.Exists(input) && !Directory.Exists(input))
                throw new ArgumentException($"Input APK or directory was not found at '{input}'.");
            request = request with { InputKind = Directory.Exists(input) ? PatchInputKind.Directory : PatchInputKind.Apk };
            if (request.InputKind == PatchInputKind.Directory) PathSafety.RejectLinks(input);
        }
        else
        {
            RequestPaths.RequireFile(request.GameAssemblyPath, "Game assembly");
            RequestPaths.RequireFile(request.MetadataPath, "Metadata");
            if (string.IsNullOrWhiteSpace(request.UnityVersion))
                throw new ArgumentException("Generating from binary/metadata requires a Unity version.");
        }
        foreach (var file in new[] { request.GameAssemblyPath, request.MetadataPath, request.Cpp2IlPath, request.Il2CppInteropCliPath }.OfType<string>())
            RequestPaths.RequireFile(file, "Interop input/tool");
        if (request.Il2CppInteropCliPath is { } tool && !Path.GetExtension(tool).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Il2CppInterop CLI override must be a managed .dll file.");
        if (request.UnityLibrariesPath is { } unity && !Directory.Exists(unity))
            throw new ArgumentException($"Unity libraries directory was not found at '{unity}'.");
        if (!string.IsNullOrWhiteSpace(request.UnityVersion))
            UnityDependenciesResolver.NormalizeVersion(request.UnityVersion);
        if (request.OutputPath is { } output)
        {
            if (File.Exists(output)) throw new ArgumentException("Interop output must be a directory.");
            RequestPaths.ValidateExport(output, request.InputPath, request.GameAssemblyPath, request.MetadataPath,
                request.UnityLibrariesPath, request.Cpp2IlPath, request.Il2CppInteropCliPath,
                request.Il2CppInteropCliPath is { } generator ? Path.GetDirectoryName(generator) : null, ToolCachePaths.Root);
        }
        return request;
    }
}
