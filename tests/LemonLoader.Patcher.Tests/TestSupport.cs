using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class TestSupport
{
    public static void MergeApk(
        string apkPath,
        string releaseRoot,
        string interopRoot,
        string? deploymentPath,
        DeploymentPolicyOptions? policies = null) =>
        PayloadAssembler.MergeApk(
            apkPath,
            new(
                releaseRoot,
                interopRoot,
                deploymentPath,
                policies ?? DeploymentPolicyOptions.Create(null, [])));

    public static void InjectDirectory(
        string gameRoot,
        string releaseRoot,
        string interopRoot,
        string? deploymentPath,
        DeploymentPolicyOptions? policies = null) =>
        PayloadAssembler.InjectDirectory(
            gameRoot,
            new(
                releaseRoot,
                interopRoot,
                deploymentPath,
                policies ?? DeploymentPolicyOptions.Create(null, [])),
            null);

    public static byte[] CreateUnityArchive()
    {
        var assemblyBytes = File.ReadAllBytes(typeof(CliApplication).Assembly.Location);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "UnityEngine.dll", "UnityEngine.CoreModule.dll" })
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(assemblyBytes);
            }
        }
        return output.ToArray();
    }

    public static string CreateReleaseTree(string root, string rid = "android-arm64")
    {
        var releaseRoot = Path.Combine(root, $"release-{Guid.NewGuid():N}");
        WritePayload(releaseRoot, "lib/arm64-v8a/libmain.so", "loader-main");
        WritePayload(releaseRoot, $"{AndroidPayloadContract.LoaderRoot}/net6/MelonLoader.dll", "loader");
        WritePayload(releaseRoot, $"{AndroidPayloadContract.LoaderRoot}/net6/MelonLoader.NativeHost.dll", "native-host");
        WritePayload(releaseRoot, $"{AndroidPayloadContract.LoaderRoot}/Dependencies/SupportModules/Il2Cpp.dll", "support");
        const string version = "11.0.0";
        var shared = $"{AndroidPayloadContract.DotnetRoot}/shared/Microsoft.NETCore.App/{version}";
        foreach (var name in new[] { "libcoreclr.so", "libclrjit.so", "System.Private.CoreLib.dll" })
            WritePayload(releaseRoot, $"{shared}/{name}", name);
        if (rid == "linux-bionic-arm64")
        {
            foreach (var name in new[] { "libSystem.Security.Cryptography.Native.OpenSsl.so", "libssl.so", "libcrypto.so" })
                WritePayload(releaseRoot, $"{shared}/{name}", name);
            WritePayload(releaseRoot, "licenses/OpenSSL/LICENSE.txt", "license");
        }
        else
            WritePayload(releaseRoot, $"{shared}/libSystem.Security.Cryptography.Native.Android.so", "android-crypto");
        WritePayload(releaseRoot, AndroidPayloadContract.PayloadManifestPath,
            JsonSerializer.Serialize(new { formatVersion = 9, runtimeRid = rid }));
        var manifest = new Dictionary<string, object>
        {
            ["formatVersion"] = 2,
            ["assetLayoutVersion"] = 9,
            ["configuration"] = "Release",
            ["gameAssembliesIncluded"] = false,
            ["managedRuntimeVersion"] = version,
            ["managedRuntimeBackend"] = "coreclr",
            ["managedRuntimeSourceRevision"] = new string('2', 40),
            ["managedRuntimeEngineFile"] = "libcoreclr.so",
            ["managedRuntimeEngineSha256"] = GetReleaseFileDigests(releaseRoot)[$"{shared}/libcoreclr.so"].Hash,
            ["runtimeRid"] = rid,
            ["minimumAndroidApi"] = 26
        };
        if (rid == "android-arm64") manifest["coreClrCryptoDexMode"] = "embedded";
        File.WriteAllText(Path.Combine(releaseRoot, "lemonloader-release.json"), JsonSerializer.Serialize(manifest));
        RefreshReleaseInventory(releaseRoot);
        return releaseRoot;
    }

    public static void RefreshReleaseInventory(string root)
    {
        var path = Path.Combine(root, "lemonloader-release.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        manifest["files"] = JsonSerializer.SerializeToNode(GetReleaseFileDigests(root)
            .Where(file => file.Key != "lemonloader-release.json")
            .Select(file => new { path = file.Key, size = file.Value.Size, sha256 = file.Value.Hash }).ToArray());
        File.WriteAllText(path, manifest.ToJsonString());
    }

    public static string CreateInteropTree(string root)
    {
        var interopRoot = Path.Combine(root, $"interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(interopRoot);
        File.WriteAllText(Path.Combine(interopRoot, "Game.dll"), "interop");
        File.WriteAllText(
            Path.Combine(interopRoot, InteropGenerationManifest.FileName),
            "{}");
        return interopRoot;
    }

    public static void CreateZip(string path, IReadOnlyDictionary<string, string> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
            WriteZipEntry(archive, entry.Key, entry.Value);
    }

    public static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    public static string ReadZipEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(
            archive.GetEntry(name)?.Open() ??
            throw new InvalidOperationException($"Missing ZIP entry '{name}'."),
            Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static HttpResponseMessage ZipResponse(byte[] archive) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(archive)
    };

    public static (string Path, long Size, string Hash) WritePayload(
        string root,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        using var input = File.OpenRead(path);
        return (
            relativePath,
            input.Length,
            Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
    }

    public static string CreateTestRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"lemonloader-patcher-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static IReadOnlyDictionary<string, (long Size, string Hash)> GetReleaseFileDigests(
        string releaseRoot) =>
        Directory.EnumerateFiles(releaseRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(releaseRoot, path).Replace('\\', '/'),
                path =>
                {
                    using var input = File.OpenRead(path);
                    return (input.Length,
                        Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
                },
                StringComparer.Ordinal);

    public static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

}

internal sealed class DelegateHandler(
    Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(handler(request));
}
