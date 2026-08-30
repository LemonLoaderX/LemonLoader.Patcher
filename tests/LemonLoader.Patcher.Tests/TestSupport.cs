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

    public static string CreateReleaseTree(
        string root,
        IReadOnlyList<string>? privateNativeLibraries = null,
        bool includeCoreClrCryptoDex = false)
    {
        var releaseRoot = Path.Combine(root, $"release-{Guid.NewGuid():N}");
        var runtimeBackend = includeCoreClrCryptoDex ? "coreclr" : "monovm-sgen";
        WritePayload(releaseRoot, "lib/arm64-v8a/libmain.so", "loader-main");
        WritePayload(
            releaseRoot,
            "assets/LemonLoader/runtime/loader/net6/MelonLoader.dll",
            "loader");
        WritePayload(
            releaseRoot,
            "assets/LemonLoader/runtime/dotnet/host/fxr/10.0.10/libhostfxr.so",
            "hostfxr");
        var runtimeEngineHash = WritePayload(
            releaseRoot,
            "assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/10.0.10/libcoreclr.so",
            "coreclr").Hash;
        var runtimeIdentity = WritePayload(
            releaseRoot,
            "assets/LemonLoader/runtime/dotnet/runtime-identity.json",
            JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                runtimeVersion = "10.0.10",
                backend = runtimeBackend,
                hostingModel = includeCoreClrCryptoDex ? "coreclr-host-api" : "hostfxr",
                engineFile = "libcoreclr.so",
                engineSha256 = runtimeEngineHash
            }));
        string? coreClrCryptoDexSha256 = null;
        if (includeCoreClrCryptoDex)
        {
            coreClrCryptoDexSha256 = WritePayload(
                releaseRoot,
                AndroidPayloadContract.CoreClrCryptoDexReleasePath,
                "crypto-dex").Hash;
        }
        foreach (var library in privateNativeLibraries ?? [])
        {
            WritePayload(
                releaseRoot,
                $"assets/LemonLoader/runtime/dotnet/native/openssl/{library}",
                $"private-{library}");
        }
        WritePayload(
            releaseRoot,
            "assets/LemonLoader/payload.json",
            JsonSerializer.Serialize(new
            {
                formatVersion = AndroidPayloadContract.FormatVersion,
                loaderSha256 = new string('0', 64),
                dotnetSha256 = new string('0', 64),
                interopSha256 = new string('0', 64),
                deploymentSha256 = new string('0', 64),
                managedRuntimeBackend = runtimeBackend,
                managedRuntimeIdentitySha256 = runtimeIdentity.Hash,
                coreClrCryptoDexSha256,
                deploymentProfile = "development",
                deploymentRevisionSha256 = ComputeDeploymentRevision([]),
                deploymentFiles = Array.Empty<object>(),
                privateNativeLibraries = privateNativeLibraries ?? []
            }));
        return releaseRoot;
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

    public static string ComputePayloadHash(ZipArchive archive, string scope)
    {
        var lines = new List<string>
        {
            $"layout-version={AndroidPayloadContract.FormatVersion}",
            $"scope={scope}"
        };
        foreach (var entry in archive.Entries
                     .Where(entry =>
                         !string.IsNullOrEmpty(entry.Name) &&
                         entry.FullName.StartsWith(
                             $"assets/LemonLoader/{scope}/",
                             StringComparison.Ordinal))
                     .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            using var input = entry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            lines.Add(
                $"{entry.FullName["assets/LemonLoader/".Length..]}|{entry.Length}|{hash}");
        }
        return HashLines(lines);
    }

    public static string ComputePayloadDirectoryHash(string releaseRoot, string scope)
    {
        var payloadRoot = Path.Combine(releaseRoot, "assets", "LemonLoader");
        var scopeRoot = Path.Combine(payloadRoot, scope);
        var files = Directory.Exists(scopeRoot)
            ? Directory.EnumerateFiles(scopeRoot, "*", SearchOption.AllDirectories)
            : [];
        var lines = new List<string>
        {
            $"layout-version={AndroidPayloadContract.FormatVersion}",
            $"scope={scope}"
        };
        foreach (var path in files.OrderBy(
                     path => Path.GetRelativePath(payloadRoot, path).Replace('\\', '/'),
                     StringComparer.Ordinal))
        {
            using var input = File.OpenRead(path);
            var relativePath = Path.GetRelativePath(payloadRoot, path).Replace('\\', '/');
            lines.Add(
                $"{relativePath}|{input.Length}|" +
                Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
        }
        return HashLines(lines);
    }

    public static string ComputeDeploymentRevision(IEnumerable<JsonElement> files)
    {
        var lines = new List<string> { "deployment-revision=1" };
        lines.AddRange(files
            .OrderBy(file => file.GetProperty("path").GetString(), StringComparer.Ordinal)
            .Select(file =>
                $"{file.GetProperty("path").GetString()}|" +
                $"{file.GetProperty("size").GetInt64()}|" +
                $"{file.GetProperty("sha256").GetString()}|" +
                file.GetProperty("policy").GetString()));
        return HashLines(lines);
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

    private static string HashLines(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', lines))))
            .ToLowerInvariant();
}

internal sealed class DelegateHandler(
    Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(handler(request));
}
