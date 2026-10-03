using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static TestSupport;
using System.Diagnostics;

if (args is ["--validate-release", var releaseArchive])
{
    var root = CreateTestRoot();
    try
    {
        ZipFile.ExtractToDirectory(Path.GetFullPath(releaseArchive), root);
        ReleaseValidator.Validate(root);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "lemonloader-release.json")));
        if (manifest.RootElement.TryGetProperty("coreClrCryptoDexMode", out var mode) &&
            mode.GetString() == "embedded" &&
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Any(path => Path.GetExtension(path).Equals(".dex", StringComparison.OrdinalIgnoreCase)))
        {
            throw new Exception("Embedded Loader release includes a standalone DEX build input.");
        }
        Console.WriteLine($"PASS: Release archive {Path.GetFullPath(releaseArchive)}");
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return;
}

if (args is ["--hold-output", var readyFile])
{
    File.WriteAllText(readyFile, Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(15));
    return;
}
if (args is ["--spawn-output-holder", var childFile])
{
    using var child = Process.Start(SelfStartInfo("--hold-output", childFile));
    return;
}
if (args is ["--terminal-output"])
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine("\u001b[32mGenerated assemblies\u001b[0m\u001b[K");
    Console.WriteLine("\u001b]8;;https://example.invalid\u0007link\u001b]8;;\u001b\\");
    Console.WriteLine("\u001b[38;2;255;128;0mRGB output\u001b[0m");
    Console.Error.WriteLine("\u009b33mwarning\u009b0m");
    Console.Error.WriteLine("\u001b[?25l\u001b[2K\u001b[?25h");
    return;
}

var apkVerificationScript = args is ["--verify-apk-script", var script] ? Path.GetFullPath(script) : null;
var tests = new (string Name, Func<Task> Run)[]
{
    ("Publication safety, cancellation and cache repair", PublicationTests.RunAsync),
    ("Unity version normalization", TestUnityVersionNormalizationAsync),
    ("Structured Unity version detection", UnityVersionDetectionTests.RunAsync),
    ("Unity dependency cache repair", TestUnityDependencyCacheRepairAsync),
    ("Unity dependency source fallback", TestUnityDependencySourceFallbackAsync),
    ("Interop generator game assembly", TestInteropGeneratorGameAssemblyAsync),
    ("Interop generator override provenance", TestInteropGeneratorOverrideAsync),
    ("Application-owned tool cache", TestToolCachePathsAsync),
    ("Release payload hash validation", TestReleaseValidationAsync),
    ("Retired payload rejection", () => TestRetiredPayloadAsync(apkVerificationScript)),
    ("Minimal layout 9 payload", () => TestMinimalPayloadAsync(apkVerificationScript)),
    ("Runtime release selection and cache isolation", TestRuntimeSelectionAsync),
    ("APK payload layout", TestApkPayloadLayoutAsync),
    ("Directory payload injection", TestDirectoryPayloadInjectionAsync),
    ("Deployment policy resolution", TestDeploymentPolicyResolutionAsync),
    ("Native library collision rejection", TestNativeLibraryCollisionAsync),
    ("Duplicate APK entry rejection", TestDuplicateApkEntryAsync),
    ("Signing password isolation", TestSigningPasswordIsolationAsync),
    ("Optional APK post-processing contract", TestPostProcessingContractAsync),
    ("External Android tool resolution", TestExternalToolResolutionAsync),
    ("CLI contract", TestCliContractAsync),
    ("Directory replacement", TestDirectoryReplacementAsync),
    ("External tool output drain cancellation", TestOutputDrainCancellationAsync),
    ("External tool plain-text output", TestTerminalOutputAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

Console.WriteLine($"LemonLoader.Patcher tests passed: {tests.Length}");
return;

static ProcessStartInfo SelfStartInfo(params string[] arguments)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(TestSupport).Assembly.Location);
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return info;
}

static async Task TestTerminalOutputAsync()
{
    var messages = new System.Collections.Concurrent.ConcurrentQueue<PatcherMessage>();
    var progress = new ToolOutputProgress(messages);
    var info = SelfStartInfo("--terminal-output");
    await ProcessRunner.RunAsync(info.FileName, progress, CancellationToken.None, info.ArgumentList.ToArray());
    var output = messages.Where(message => message.Kind == PatcherMessageKind.ToolOutput)
        .Select(message => message.Text).Order(StringComparer.Ordinal).ToArray();
    AssertTrue(output.SequenceEqual(new[] { "Generated assemblies", "RGB output", "link", "warning" }),
        "Terminal control sequences leaked into tool log: " + string.Join(" | ", output));
}

static Task TestToolCachePathsAsync()
{
    var root = Path.Combine(AppContext.BaseDirectory, ".tools");
    var first = new PatchRequest { InputPath = "one/game.apk", OutputPath = "one/output.apk" };
    var second = new PatchRequest { InputPath = "two/game.apk", OutputPath = "elsewhere/output.apk" };
    var directory = new PatchRequest { InputPath = "unpacked/game" };
    foreach (var request in new[] { first, second, directory })
        AssertEqual(root, request.ToolCacheRoot);
    var unity = new UnityDependenciesRequest { UnityVersion = "2021.3.0f1", OutputPath = "exports/unity" };
    AssertEqual(Path.Combine(root, "UnityDependencies"), unity.CacheRoot);
    AssertEqual(Path.GetFullPath("explicit-cache"), (unity with { CachePath = "explicit-cache" }).CacheRoot);
    var fixture = CreateTestRoot();
    try
    {
        var input = Path.Combine(fixture, "game.apk");
        File.WriteAllText(input, "fixture");
        AssertThrows<ArgumentException>(() => new PatchRequest
        {
            InputPath = input, OutputPath = Path.Combine(fixture, "output.apk"),
            InteropOutputPath = Path.Combine(root, "destructive-export")
        }.NormalizeAndValidate());
    }
    finally { Directory.Delete(fixture, true); }
    return Task.CompletedTask;
}

static async Task TestOutputDrainCancellationAsync()
{
    var root = CreateTestRoot();
    var readyFile = Path.Combine(root, "child.pid");
    using var cancellation = new CancellationTokenSource();
    var info = SelfStartInfo("--spawn-output-holder", readyFile);
    var run = ProcessRunner.RunAsync(info.FileName, null, cancellation.Token, info.ArgumentList.ToArray());
    try
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(readyFile) && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        if (!File.Exists(readyFile)) throw new Exception("Output holder did not start.");
        await Task.Delay(200); // Let the direct child exit while its descendant holds both pipes.
        if (run.IsCompleted) throw new Exception("Inherited output pipes were not held open.");
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await run.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    finally
    {
        cancellation.Cancel();
        if (File.Exists(readyFile))
        {
            try
            {
                using var child = Process.GetProcessById(int.Parse(File.ReadAllText(readyFile)));
                if (!child.HasExited) child.Kill(true);
                await child.WaitForExitAsync();
            }
            catch (ArgumentException) { } // Holder already exited.
        }
        Directory.Delete(root, true);
    }
}

static async Task TestRuntimeSelectionAsync()
{
    var root = CreateTestRoot();
    try
    {
        AssertEqual("android", RuntimeVariants.Normalize(null));
        AssertThrows<ArgumentException>(() => RuntimeVariants.Normalize("linux-x64"));
        foreach (var variant in new[] { "android", "bionic" })
        {
            var rid = variant == "android" ? "android-arm64" : "linux-bionic-arm64";
            using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(new { runtimeRid = rid }));
            RuntimeVariants.ValidateManifest(manifest.RootElement, variant);
            using var oldManifest = JsonDocument.Parse(JsonSerializer.Serialize(new { experimentalRuntimeRid = rid }));
            AssertThrows<InvalidDataException>(() => RuntimeVariants.ValidateManifest(oldManifest.RootElement, variant));
            AssertThrows<InvalidDataException>(() => RuntimeVariants.ValidateManifest(
                manifest.RootElement, variant == "android" ? "bionic" : "android"));
            var cached = Path.Combine(root, RuntimeVariants.ArchiveName(variant));
            ZipFile.CreateFromDirectory(CreateReleaseTree(root, rid), cached);
            AssertEqual(cached, await ReleaseResolver.ResolveLatestAsync(root, runtimeVariant: variant));
        }
        var input = Path.Combine(root, "input.apk");
        File.WriteAllText(input, "fixture");
        var request = CliRequestParser.ParsePatchRequest([input, "--output", Path.Combine(root, "output.apk"), "--runtime", "bionic"]);
        AssertEqual("bionic", request.RuntimeVariant);
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [input, "--output", Path.Combine(root, "output.apk"), "--runtime", "other"]));
    }
    finally { Directory.Delete(root, true); }
}

static Task TestUnityVersionNormalizationAsync()
{
    AssertEqual("6000.3.8", UnityDependenciesResolver.NormalizeVersion("6000.3.8f1"));
    AssertEqual("2022.3.62", UnityDependenciesResolver.NormalizeVersion("2022.3.62"));
    AssertThrows<InvalidDataException>(() =>
        UnityDependenciesResolver.NormalizeVersion("../../6000.3.8"));
    var root = Path.GetFullPath("UnityDependencies");
    return AssertThrowsAsync<ArgumentException>(() =>
        UnityDependenciesPipeline.RunAsync(new()
        {
            UnityVersion = "6000.3.8f1",
            OutputPath = root,
            CachePath = Path.Combine(root, ".cache")
        }));
}

static Task TestInteropGeneratorGameAssemblyAsync()
{
    var input = Path.Combine("root", "input");
    var arguments = GameInteropGenerator.BuildGeneratorArguments(
        "Il2CppInterop.CLI.dll",
        input,
        Path.Combine("root", "cpp2il"),
        Path.Combine("root", "output"),
        Path.Combine("root", "unity"));
    AssertEqual("--roll-forward", arguments[0]);
    AssertEqual("Major", arguments[1]);
    AssertEqual("Il2CppInterop.CLI.dll", arguments[2]);
    var optionIndex = Array.IndexOf(arguments, "--game-assembly");
    AssertTrue(optionIndex >= 0, "Interop generation must receive the game assembly.");
    AssertEqual(Path.Combine(input, "libil2cpp.so"), arguments[optionIndex + 1]);
    return Task.CompletedTask;
}
static Task TestInteropGeneratorOverrideAsync()
{
    var root = CreateTestRoot();
    try
    {
        var toolPath = Path.Combine(root, "Il2CppInterop.CLI.dll");
        File.Copy(typeof(ApkPatchPipeline).Assembly.Location, toolPath);
        var generatorPath = Path.Combine(root, "Il2CppInterop.Generator.dll");
        File.WriteAllText(generatorPath, "generator-v1");
        var provenancePath = Path.Combine(
            root,
            BundledInteropGeneratorTool.ProvenanceFileName);
        var provenance = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            revision = BundledInteropGeneratorTool.Revision
        });
        File.WriteAllText(provenancePath, provenance);
        var tool = InteropGeneratorTool.FromOverride(toolPath);
        AssertEqual(Path.GetFullPath(toolPath), tool.Path);
        AssertEqual("override", tool.Source);
        AssertTrue(!string.IsNullOrWhiteSpace(tool.Version), "The override tool version was not detected.");
        var bundledTool = InteropGeneratorTool.FromBundledFork(toolPath);
        AssertEqual("bundled-fork", bundledTool.Source);
        File.WriteAllText(
            provenancePath,
            JsonSerializer.Serialize(new { formatVersion = 1, revision = new string('0', 40) }));
        AssertThrows<InvalidDataException>(() =>
            InteropGeneratorTool.FromBundledFork(toolPath));
        File.WriteAllText(provenancePath, provenance);
        AssertTrue(
            BundledInteropGeneratorTool.Revision.Length == 40 &&
            BundledInteropGeneratorTool.Revision.All(Uri.IsHexDigit),
            "The bundled generator revision is not a full Git commit ID.");
        AssertEqual(
            "https://github.com/LemonLoaderX/LemonLoader/releases/latest/download/LemonLoader-runtime-android-arm64.zip",
            ReleaseResolver.LatestUrl);
        File.WriteAllText(generatorPath, "generator-v2");
        var changedTool = InteropGeneratorTool.FromOverride(toolPath);
        AssertTrue(
            changedTool.ContentSha256 != tool.ContentSha256,
            "The tool content hash did not include the generator dependency.");
        File.WriteAllText(generatorPath, "generator-v1");

        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        var unity = Path.Combine(root, "unity");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(unity);
        File.WriteAllText(Path.Combine(input, "libil2cpp.so"), "game-assembly");
        File.WriteAllText(Path.Combine(input, "global-metadata.dat"), "metadata");
        File.WriteAllText(Path.Combine(output, "Game.dll"), "generated");
        var cpp2Il = Path.Combine(root, "Cpp2IL.exe");
        File.WriteAllText(cpp2Il, "cpp2il");

        InteropGenerationManifest.Write(
            output,
            input,
            "6000.3.8f1",
            new UnityDependenciesResolution(
                unity,
                "6000.3.8f1",
                "6000.3.8",
                "fixture",
                null,
                null,
                1,
                new string('1', 64)),
            cpp2Il,
            "fixture",
            tool);

        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(output, InteropGenerationManifest.FileName)));
        AssertTrue(!document.RootElement.TryGetProperty("cacheKey", out _), "Interop export retains a removed cache identity.");
        var tools = document.RootElement.GetProperty("tools");
        AssertEqual("override", tools.GetProperty("il2CppInteropSource").GetString());
        AssertEqual(tool.Version, tools.GetProperty("il2CppInteropVersion").GetString());
        AssertEqual(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(toolPath))).ToLowerInvariant(),
            tools.GetProperty("il2CppInteropSha256").GetString());
        AssertEqual(
            tool.ContentSha256,
            tools.GetProperty("il2CppInteropContentSha256").GetString());
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestSigningPasswordIsolationAsync()
{
    const string storePassword = "store-secret";
    const string keyPassword = "key-secret";
    var invocation = ApkPostProcessor.CreateSigningInvocation(
        new SigningOptions("signing.jks", storePassword, "alias", keyPassword),
        "signed.apk",
        "aligned.apk");
    AssertTrue(
        invocation.Arguments.All(argument =>
            !argument.Contains(storePassword, StringComparison.Ordinal) &&
            !argument.Contains(keyPassword, StringComparison.Ordinal)),
        "Signing passwords must not be placed on the process command line.");
    AssertTrue(
        invocation.Arguments.Contains("env:LEMONLOADER_APKSIGNER_STORE_PASSWORD"),
        "The store password must be supplied through the child environment.");
    AssertEqual(
        storePassword,
        invocation.Environment["LEMONLOADER_APKSIGNER_STORE_PASSWORD"]);
    AssertEqual(
        keyPassword,
        invocation.Environment["LEMONLOADER_APKSIGNER_KEY_PASSWORD"]);
    return Task.CompletedTask;
}

static async Task TestUnityDependencyCacheRepairAsync()
{
    var root = CreateTestRoot();
    try
    {
        var cache = Path.Combine(root, "cache");
        var corrupt = Path.Combine(cache, "6000.3.8");
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "UnityEngine.dll"), "not an assembly");

        var archive = CreateUnityArchive();
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            return ZipResponse(archive);
        }));
        var sources = new[]
        {
            new UnityDependencySource("test-primary", "https://primary.invalid/{0}.zip")
        };
        var result = await UnityDependenciesResolver.ResolveAsync(
            cache,
            "6000.3.8f1",
            client,
            sources,
            CancellationToken.None);

        AssertEqual(1, requests);
        AssertEqual("6000.3.8", result.PackageVersion);
        AssertTrue(result.AssemblyCount >= 2, "Expected the restored Unity assemblies.");
        AssertTrue(
            File.Exists(Path.Combine(result.DirectoryPath, UnityDependenciesResolver.ManifestFileName)),
            "Expected a Unity dependency provenance manifest.");

        using var offlineClient = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("A valid cache must not access the network.")));
        var cached = await UnityDependenciesResolver.ResolveAsync(
            cache,
            "6000.3.8f1",
            offlineClient,
            sources,
            CancellationToken.None);
        AssertEqual(result.ArchiveSha256, cached.ArchiveSha256);

        File.Copy(
            typeof(DelegateHandler).Assembly.Location,
            Path.Combine(cached.DirectoryPath, "UnityEngine.CoreModule.dll"),
            true);
        var repaired = await UnityDependenciesResolver.ResolveAsync(
            cache,
            "6000.3.8f1",
            offlineClient,
            sources,
            CancellationToken.None);
        AssertEqual(result.ContentSha256, repaired.ContentSha256);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task TestUnityDependencySourceFallbackAsync()
{
    var root = CreateTestRoot();
    try
    {
        var archive = CreateUnityArchive();
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            return request.RequestUri!.Host == "primary.invalid"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ZipResponse(archive);
        }));
        var sources = new[]
        {
            new UnityDependencySource("primary", "https://primary.invalid/{0}.zip"),
            new UnityDependencySource("fallback", "https://fallback.invalid/{0}.zip")
        };
        var result = await UnityDependenciesResolver.ResolveAsync(
            Path.Combine(root, "cache"),
            "6000.3.8f1",
            client,
            sources,
            CancellationToken.None);
        AssertEqual(2, requests);
        AssertEqual("fallback", result.Source);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static Task TestReleaseValidationAsync()
{
    var root = CreateTestRoot();
    try
    {
        foreach (var rid in new[] { "android-arm64", "linux-bionic-arm64" })
        {
            var release = CreateReleaseTree(root, rid);
            ReleaseValidator.Validate(release);
            var manifestPath = Path.Combine(release, "lemonloader-release.json");
            var original = File.ReadAllText(manifestPath);
            void RejectMetadata(string key, object value)
            {
                var manifest = JsonNode.Parse(original)!;
                manifest[key] = JsonSerializer.SerializeToNode(value);
                File.WriteAllText(manifestPath, manifest.ToJsonString());
                AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
                File.WriteAllText(manifestPath, original);
            }
            RejectMetadata("assetLayoutVersion", 8);
            RejectMetadata("managedRuntimeBackend", "monovm-sgen");
            RejectMetadata("managedRuntimeBackend", "unknown");
            RejectMetadata("runtimeRid", "linux-x64");
            RejectMetadata("minimumAndroidApi", 25);
            RejectMetadata("managedRuntimeSourceRevision", "invalid");
            RejectMetadata("managedRuntimeEngineSha256", new string('0', 64));
            RejectMetadata("coreClrCryptoDexMode", "external");
            RejectMetadata("gameAssembliesIncluded", true);

            var manifest = JsonNode.Parse(original)!;
            manifest["producer"] = "future";
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            ReleaseValidator.Validate(release);
            File.WriteAllText(manifestPath, original);
            var shared = "assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/11.0.0";
            var required = new List<string> { $"{shared}/libcoreclr.so", $"{shared}/libclrjit.so",
                $"{shared}/System.Private.CoreLib.dll" };
            required.AddRange(rid == "android-arm64"
                ? new[] { $"{shared}/libSystem.Security.Cryptography.Native.Android.so" }
                : new[] { $"{shared}/libSystem.Security.Cryptography.Native.OpenSsl.so", $"{shared}/libssl.so",
                    $"{shared}/libcrypto.so", "licenses/OpenSSL/LICENSE.txt" });
            foreach (var path in required)
            {
                var file = GamePackageLayout.FilePath(release, path);
                var bytes = File.ReadAllBytes(file);
                File.Delete(file);
                RefreshReleaseInventory(release);
                AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
                File.WriteAllBytes(file, []);
                RefreshReleaseInventory(release);
                AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
                File.WriteAllBytes(file, bytes);
                RefreshReleaseInventory(release);
            }
            foreach (var path in new[] { "assets/outside/file", "lib/arm64-v8a/extra.so",
                "assets/LemonLoader/runtime/unknown/file", "assets/LemonLoader/deployment/Mods/mod.dll",
                "assets/LemonLoader/runtime/interop/Game.dll",
                $"{shared}/" + (rid == "android-arm64"
                    ? "libSystem.Security.Cryptography.Native.OpenSsl.so"
                    : "libSystem.Security.Cryptography.Native.Android.so") })
            {
                WritePayload(release, path, "unexpected");
                RefreshReleaseInventory(release);
                AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
                File.Delete(GamePackageLayout.FilePath(release, path));
                RefreshReleaseInventory(release);
            }
            var payloadPath = GamePackageLayout.FilePath(release, AndroidPayloadContract.PayloadManifestPath);
            var payload = File.ReadAllText(payloadPath);
            foreach (var invalid in new[] {
                new { formatVersion = 8, runtimeRid = rid },
                new { formatVersion = 9, runtimeRid = "invalid" } })
            {
                File.WriteAllText(payloadPath, JsonSerializer.Serialize(invalid));
                RefreshReleaseInventory(release);
                AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
            }
            File.WriteAllText(payloadPath, payload);
            RefreshReleaseInventory(release);
            var current = File.ReadAllText(manifestPath);
            var invalidPath = JsonNode.Parse(current)!;
            invalidPath["files"]![0]!["path"] = "../escape";
            File.WriteAllText(manifestPath, invalidPath.ToJsonString());
            AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
            var duplicate = JsonNode.Parse(current)!;
            duplicate["files"]!.AsArray().Add(duplicate["files"]![0]!.DeepClone());
            File.WriteAllText(manifestPath, duplicate.ToJsonString());
            AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
            File.WriteAllText(manifestPath, current);
            File.AppendAllText(Path.Combine(release, "lib/arm64-v8a/libmain.so"), "corrupt");
            AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
        }
    }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}

static async Task TestRetiredPayloadAsync(string? verificationScript)
{
    var root = CreateTestRoot();
    try
    {
        var release = CreateReleaseTree(root);
        WritePayload(release, AndroidPayloadContract.PayloadManifestPath,
            JsonSerializer.Serialize(new { formatVersion = 8, runtimeRid = "android-arm64" }));
        var apk = Path.Combine(root, "retired.apk");
        CreateZip(apk, new Dictionary<string, string> { ["classes.dex"] = "original" });
        var before = File.ReadAllBytes(apk);
        AssertThrows<InvalidDataException>(() => MergeApk(apk, release, CreateInteropTree(root), null));
        AssertTrue(before.SequenceEqual(File.ReadAllBytes(apk)), "Retired input changed the original APK.");
        var game = Path.Combine(root, "directory");
        WritePayload(game, "lib/arm64-v8a/libmain.so", "game-main");
        WritePayload(game, "lib/arm64-v8a/libunity.so", "unity");
        AssertThrows<InvalidDataException>(() => InjectDirectory(game, release, CreateInteropTree(root), null));
        AssertEqual("game-main", File.ReadAllText(Path.Combine(game, "lib/arm64-v8a/libmain.so")));
        if (verificationScript is not null)
        {
            using (var archive = ZipFile.Open(apk, ZipArchiveMode.Update))
            {
                WriteZipEntry(archive, "lib/arm64-v8a/libmain.so", "bootstrap");
                WriteZipEntry(archive, AndroidPayloadContract.PayloadManifestPath,
                    JsonSerializer.Serialize(new { formatVersion = 8, runtimeRid = "android-arm64" }));
            }
            await AssertThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunAsync("pwsh", null,
                CancellationToken.None, "-NoProfile", "-File", verificationScript, "-ApkPath", apk));
        }
    }
    finally { Directory.Delete(root, true); }
}

static async Task TestMinimalPayloadAsync(string? verificationScript)
{
    var root = CreateTestRoot();
    try
    {
        foreach (var rid in new[] { "android-arm64", "linux-bionic-arm64" })
        {
            var release = CreateMinimalReleaseFixture(root, rid);
            ReleaseValidator.Validate(release);
            var interop = CreateInteropTree(root);
            File.Delete(Path.Combine(interop, InteropGenerationManifest.FileName));
            var deployment = Path.Combine(root, "deployment-" + rid);
            WritePayload(deployment, "Mods/Example.dll", "mod");
            WritePayload(deployment, "Mods/Optional.dll", "optional");
            WritePayload(deployment, "Mods/Required.dll", "required");
            WritePayload(deployment, "UserData/config", "config");
            WritePayload(deployment, "Future/file", "future");
            var policies = DeploymentPolicyOptions.Create("production",
                ["Mods/Optional.dll=seed", "Mods/Required.dll=enforce"]);
            var apk = Path.Combine(root, rid + ".apk");
            CreateZip(apk, new Dictionary<string, string> { ["classes.dex"] = "game", ["classes3.dex"] = "secondary" });
            MergeApk(apk, release, interop, deployment, policies);
            using (var archive = ZipFile.OpenRead(apk))
            {
                using var payload = JsonDocument.Parse(ReadZipEntry(archive, AndroidPayloadContract.PayloadManifestPath));
                AssertEqual(9, payload.RootElement.GetProperty("formatVersion").GetInt32());
                AssertEqual(rid, payload.RootElement.GetProperty("runtimeRid").GetString());
                AssertEqual(3, payload.RootElement.EnumerateObject().Count());
                var overrides = payload.RootElement.GetProperty("deploymentFiles").EnumerateArray()
                    .ToDictionary(file => file.GetProperty("path").GetString()!, file => file.GetProperty("policy").GetString()!);
                AssertEqual(3, overrides.Count);
                AssertEqual("refresh", overrides["Mods/Example.dll"]);
                AssertEqual("enforce", overrides["Mods/Required.dll"]);
                AssertEqual("upgrade", overrides["UserData/config"]);
                AssertEqual(2, archive.Entries.Count(entry => entry.FullName.EndsWith(".dex")));
                AssertTrue(archive.GetEntry(AndroidPayloadContract.InteropRoot + "/interop-manifest.json") is null,
                    "Layout 9 copied a generation manifest into the APK.");
                AssertTrue(archive.GetEntry(AndroidPayloadContract.DotnetRoot + "/runtime-identity.json") is null,
                    "Layout 9 requires a runtime identity JSON.");
            }
            if (verificationScript is not null)
            {
                await ProcessRunner.RunAsync("pwsh", null, CancellationToken.None,
                    "-NoProfile", "-File", verificationScript, "-ApkPath", apk,
                    "-ExpectedDeploymentProfile", "production", "-ExpectedDeploymentPolicy",
                    "Mods/Optional.dll=seed,Mods/Required.dll=enforce");
                using (var archive = ZipFile.Open(apk, ZipArchiveMode.Update))
                {
                    archive.GetEntry(AndroidPayloadContract.DeploymentRoot + "/Mods/Example.dll")!.Delete();
                    WriteZipEntry(archive, AndroidPayloadContract.DeploymentRoot + "/Mods/Manual.dll", "manual add");
                }
                await ProcessRunner.RunAsync("pwsh", null, CancellationToken.None,
                    "-NoProfile", "-File", verificationScript, "-ApkPath", apk,
                    "-ExpectedDeployment", "Mods/Manual.dll", "-ExpectedDeploymentPolicy", "Mods/Manual.dll=seed");
                using (var archive = ZipFile.Open(apk, ZipArchiveMode.Update))
                {
                    archive.GetEntry(AndroidPayloadContract.PayloadManifestPath)!.Delete();
                    WriteZipEntry(archive, AndroidPayloadContract.PayloadManifestPath,
                        JsonSerializer.Serialize(new { formatVersion = 9, runtimeRid = rid,
                            deploymentFiles = new[] { new { path = "../escape", policy = "seed" } } }));
                }
                await AssertThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunAsync("pwsh", null, CancellationToken.None,
                    "-NoProfile", "-File", verificationScript, "-ApkPath", apk));
            }
            var game = Path.Combine(root, "directory-" + rid);
            WritePayload(game, "lib/arm64-v8a/libmain.so", "game-main");
            WritePayload(game, "lib/arm64-v8a/libunity.so", "game-unity");
            InjectDirectory(game, release, interop, deployment, policies);
            AssertTrue(File.Exists(Path.Combine(game, "assets/LemonLoader/runtime/interop/Game.dll")), "Plain Interop DLL injection failed.");
            AssertEqual(0, Directory.GetFiles(game, "*.dex").Length);
            var wrongApi = CreateMinimalReleaseFixture(root, rid, minimumApi: 25);
            AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(wrongApi));
            WritePayload(release, "lib/arm64-v8a/libmain.so", "corrupted-bootstrap");
            AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
        }
    }
    finally { Directory.Delete(root, true); }
}

static string CreateMinimalReleaseFixture(string root, string rid, int minimumApi = 26)
{
    var release = CreateReleaseTree(root, rid);
    var manifestPath = Path.Combine(release, "lemonloader-release.json");
    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
    manifest["minimumAndroidApi"] = minimumApi;
    File.WriteAllText(manifestPath, manifest.ToJsonString());
    return release;
}

static Task TestApkPayloadLayoutAsync()
{
    var root = CreateTestRoot();
    try
    {
        var apkPath = Path.Combine(root, "game.apk");
        CreateZip(apkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["classes.dex"] = "game-classes",
            ["classes2.dex"] = "game-classes-2"
        });
        var releaseRoot = CreateReleaseTree(root);
        var interopRoot = CreateInteropTree(root);
        var deploymentRoot = Path.Combine(root, "deployment");
        Directory.CreateDirectory(Path.Combine(deploymentRoot, "Mods"));
        Directory.CreateDirectory(Path.Combine(deploymentRoot, "Plugins"));
        Directory.CreateDirectory(Path.Combine(deploymentRoot, "UserLibs"));
        Directory.CreateDirectory(Path.Combine(deploymentRoot, "UserData", "Fonts"));
        Directory.CreateDirectory(Path.Combine(deploymentRoot, "Custom"));
        File.WriteAllText(Path.Combine(deploymentRoot, "Mods", "ExampleMod.dll"), "mod");
        File.WriteAllText(Path.Combine(deploymentRoot, "Plugins", "ExamplePlugin.dll"), "plugin");
        File.WriteAllText(Path.Combine(deploymentRoot, "UserLibs", "SharedLibrary.dll"), "user-lib");
        File.WriteAllText(
            Path.Combine(deploymentRoot, "UserData", "Fonts", "font.ab"),
            "font-bundle");
        File.WriteAllText(Path.Combine(deploymentRoot, "Custom", "fixture.bin"), "future-input");

        MergeApk(apkPath, releaseRoot, interopRoot, deploymentRoot,
            DeploymentPolicyOptions.Create(
            "production",
            ["UserData/Fonts/font.ab=enforce"]));

        using var archive = ZipFile.OpenRead(apkPath);
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/runtime/interop/Game.dll") is not null,
            "Interop assembly was not stored in its independent runtime domain.");
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/deployment/Mods/ExampleMod.dll") is not null,
            "Packaged Mod was not stored in the consolidated deployment tree.");
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/deployment/Plugins/ExamplePlugin.dll") is not null,
            "Packaged Plugin was not stored in the deployment tree.");
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/deployment/UserLibs/SharedLibrary.dll") is not null,
            "Packaged UserLib was not stored in the deployment tree.");
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/deployment/UserData/Fonts/font.ab") is not null,
            "Nested UserData was not stored with its relative path.");
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/deployment/Custom/fixture.bin") is not null,
            "A deployment root did not preserve a future top-level directory.");
        AssertTrue(
            archive.GetEntry("assets/LemonLoader/payload.json") is not null,
            "The consolidated payload manifest is missing.");
        AssertEqual("game-classes", ReadZipEntry(archive, "classes.dex"));
        AssertEqual("game-classes-2", ReadZipEntry(archive, "classes2.dex"));
        AssertTrue(archive.GetEntry("classes3.dex") is null, "Embedded injection added a DEX.");
        AssertTrue(
            archive.GetEntry(
                "assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/11.0.0/" +
                "lemonloader-coreclr-crypto.dex") is null,
            "The Patcher duplicated the CoreCLR crypto helper dex in the runtime domain.");
        using var payloadDocument = JsonDocument.Parse(
            ReadZipEntry(archive, "assets/LemonLoader/payload.json"));
        AssertEqual(9, payloadDocument.RootElement.GetProperty("formatVersion").GetInt32());
        AssertEqual(3, payloadDocument.RootElement.EnumerateObject().Count());
        var deploymentFiles = payloadDocument.RootElement.GetProperty("deploymentFiles").EnumerateArray()
            .ToDictionary(item => item.GetProperty("path").GetString()!,
                item => item.GetProperty("policy").GetString()!, StringComparer.Ordinal);
        AssertEqual("refresh", deploymentFiles["Mods/ExampleMod.dll"]);
        AssertEqual("refresh", deploymentFiles["Plugins/ExamplePlugin.dll"]);
        AssertEqual("refresh", deploymentFiles["UserLibs/SharedLibrary.dll"]);
        AssertEqual("enforce", deploymentFiles["UserData/Fonts/font.ab"]);
        AssertTrue(!deploymentFiles.ContainsKey("Custom/fixture.bin"), "Seed should use the default.");
        foreach (var file in payloadDocument.RootElement.GetProperty("deploymentFiles").EnumerateArray())
            AssertEqual(2, file.EnumerateObject().Count());

        var alreadyPatchedApkPath = Path.Combine(root, "already-patched.apk");
        CreateZip(alreadyPatchedApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["assets/LemonLoader/payload.json"] = "existing-loader"
        });
        AssertThrows<InvalidDataException>(() => MergeApk(
            alreadyPatchedApkPath,
            releaseRoot,
            interopRoot,
            null));

        var invalidApkPath = Path.Combine(root, "invalid-casing.apk");
        CreateZip(invalidApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main"
        });
        var invalidDeploymentRoot = Path.Combine(root, "invalid-deployment");
        Directory.CreateDirectory(Path.Combine(invalidDeploymentRoot, "userdata"));
        File.WriteAllText(Path.Combine(invalidDeploymentRoot, "userdata", "font.ab"), "bad-casing");
        AssertThrows<InvalidDataException>(() => MergeApk(
            invalidApkPath,
            releaseRoot,
            interopRoot,
            invalidDeploymentRoot));

        var collisionApkPath = Path.Combine(root, "deployment-collision.apk");
        CreateZip(collisionApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main"
        });
        var collisionRoot = Path.Combine(root, "collision-deployment");
        Directory.CreateDirectory(Path.Combine(collisionRoot, "Mods"));
        File.WriteAllText(Path.Combine(collisionRoot, "Mods", "ExampleMod.dll"), "replacement");
        WritePayload(
            releaseRoot,
            "assets/LemonLoader/deployment/Mods/ExampleMod.dll",
            "release-owned");
        AssertThrows<InvalidDataException>(() => MergeApk(
            collisionApkPath,
            releaseRoot,
            interopRoot,
            collisionRoot));

        var reservedApkPath = Path.Combine(root, "reserved-deployment.apk");
        CreateZip(reservedApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main"
        });
        var reservedRoot = Path.Combine(root, "reserved-deployment");
        Directory.CreateDirectory(Path.Combine(reservedRoot, ".lemonloader-backups"));
        File.WriteAllText(
            Path.Combine(reservedRoot, ".lemonloader-backups", "payload.bin"),
            "reserved");
        AssertThrows<InvalidDataException>(() => MergeApk(
            reservedApkPath,
            releaseRoot,
            interopRoot,
            reservedRoot));

        var documentationApkPath = Path.Combine(root, "documentation.apk");
        CreateZip(documentationApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main"
        });
        var documentationRelease = CreateReleaseTree(root);
        WritePayload(
            documentationRelease,
            "assets/LemonLoader/runtime/loader/Documentation/README.md",
            "desktop documentation");
        MergeApk(
            documentationApkPath,
            documentationRelease,
            interopRoot,
            null);
        using var documentationArchive = ZipFile.OpenRead(documentationApkPath);
        AssertTrue(
            documentationArchive.GetEntry(
                "assets/LemonLoader/runtime/loader/Documentation/README.md") is not null,
            "The Patcher rejected or removed an additive Release file.");
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestDirectoryPayloadInjectionAsync()
{
    var root = CreateTestRoot();
    try
    {
        var gameRoot = Path.Combine(root, "unpacked-game");
        WritePayload(gameRoot, "lib/arm64-v8a/libmain.so", "game-main");
        WritePayload(gameRoot, "lib/arm64-v8a/libunity.so", "unity");
        WritePayload(gameRoot, "lib/arm64-v8a/libil2cpp.so", "il2cpp");
        WritePayload(gameRoot, "classes.dex", "game-classes");
        WritePayload(
            gameRoot,
            "assets/bin/Data/Managed/Metadata/global-metadata.dat",
            "metadata");
        WritePayload(gameRoot, "res/keep.txt", "untouched");
        var releaseRoot = CreateReleaseTree(root);
        var interopRoot = CreateInteropTree(root);
        var deploymentRoot = Path.Combine(root, "directory-deployment");
        WritePayload(deploymentRoot, "Mods/ExampleMod.dll", "mod");

        InjectDirectory(
            gameRoot,
            releaseRoot,
            interopRoot,
            deploymentRoot,
            DeploymentPolicyOptions.Create("production", []));

        AssertEqual("untouched", File.ReadAllText(Path.Combine(gameRoot, "res", "keep.txt"), Encoding.UTF8));
        AssertEqual("loader-main", File.ReadAllText(
            Path.Combine(gameRoot, "lib", "arm64-v8a", "libmain.so"),
            Encoding.UTF8));
        AssertTrue(
            File.Exists(Path.Combine(
                gameRoot,
                "assets",
                "LemonLoader",
                "runtime",
                "interop",
                "Game.dll")),
            "Interop was not injected into the original directory.");
        AssertTrue(
            File.Exists(Path.Combine(
                gameRoot,
                "assets",
                "LemonLoader",
                "deployment",
                "Mods",
                "ExampleMod.dll")),
            "Deployment was not injected into the original directory.");
        AssertEqual("game-classes", File.ReadAllText(Path.Combine(gameRoot, "classes.dex")));
        AssertTrue(!File.Exists(Path.Combine(gameRoot, "classes2.dex")), "Directory injection added a DEX.");
        AssertTrue(
            Directory.GetDirectories(gameRoot, ".lemonloader-patcher-*", SearchOption.TopDirectoryOnly).Length == 0,
            "Directory injection left a transaction directory behind.");

        using var payload = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            gameRoot,
            "assets",
            "LemonLoader",
            "payload.json")));
        AssertEqual(9, payload.RootElement.GetProperty("formatVersion").GetInt32());
        AssertEqual("refresh", payload.RootElement.GetProperty("deploymentFiles")[0].GetProperty("policy").GetString());

        AssertThrows<InvalidDataException>(() => InjectDirectory(
            gameRoot,
            releaseRoot,
            interopRoot,
            null));

        var decodedGameRoot = Path.Combine(root, "apktool-decoded-game");
        WritePayload(decodedGameRoot, "lib/arm64-v8a/libmain.so", "game-main");
        WritePayload(decodedGameRoot, "lib/arm64-v8a/libunity.so", "unity");
        WritePayload(decodedGameRoot, "lib/arm64-v8a/libil2cpp.so", "il2cpp");
        WritePayload(decodedGameRoot, "smali/Main.smali", "primary-smali");
        WritePayload(decodedGameRoot, "smali_classes2/Secondary.smali", "secondary-smali");

        InjectDirectory(
            decodedGameRoot,
            releaseRoot,
            interopRoot,
            null);

        AssertTrue(!File.Exists(Path.Combine(decodedGameRoot, "classes3.dex")), "Decoded injection added a DEX.");
        AssertTrue(
            !File.Exists(Path.Combine(decodedGameRoot, "classes.dex")) &&
            !File.Exists(Path.Combine(decodedGameRoot, "classes2.dex")),
            "Directory injection materialized synthetic source DEX files.");
        AssertTrue(
            File.Exists(Path.Combine(decodedGameRoot, "smali", "Main.smali")) &&
            File.Exists(Path.Combine(
                decodedGameRoot,
                "smali_classes2",
                "Secondary.smali")),
            "Directory injection modified apktool smali source directories.");

        var missingPrimaryDexRoot = Path.Combine(root, "missing-primary-dex");
        WritePayload(missingPrimaryDexRoot, "lib/arm64-v8a/libmain.so", "game-main");
        WritePayload(missingPrimaryDexRoot, "lib/arm64-v8a/libunity.so", "unity");
        WritePayload(missingPrimaryDexRoot, "lib/arm64-v8a/libil2cpp.so", "il2cpp");
        WritePayload(
            missingPrimaryDexRoot,
            "smali_classes2/Secondary.smali",
            "secondary-smali");
        InjectDirectory(missingPrimaryDexRoot, releaseRoot, interopRoot, null);
        AssertEqual(0, Directory.GetFiles(missingPrimaryDexRoot, "*.dex").Length);

        var collisionRoot = Path.Combine(root, "native-collision");
        WritePayload(collisionRoot, "lib/arm64-v8a/libmain.so", "original-main");
        WritePayload(collisionRoot, "lib/arm64-v8a/libunity.so", "unity");
        WritePayload(collisionRoot, "lib/arm64-v8a/extra.so", "game-extra");
        var collisionRelease = CreateReleaseTree(root);
        WritePayload(collisionRelease, "lib/arm64-v8a/extra.so", "release-extra");
        AssertThrows<InvalidDataException>(() => InjectDirectory(
            collisionRoot, collisionRelease, interopRoot, null));
        AssertEqual("original-main", File.ReadAllText(Path.Combine(collisionRoot, "lib/arm64-v8a/libmain.so")));
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestDeploymentPolicyResolutionAsync()
{
    var development = DeploymentPolicyOptions.Create(null, []);
    AssertEqual(DeploymentFilePolicy.Seed, development.Resolve("Mods/Test.dll"));
    AssertEqual(DeploymentFilePolicy.Seed, development.Resolve("UserData/config.cfg"));
    AssertEqual(DeploymentFilePolicy.Seed, development.Resolve("Future/file.bin"));

    var production = DeploymentPolicyOptions.Create(
        "production",
        [
            "Mods/**=upgrade",
            "Mods/Optional/**=seed",
            "Mods/Required.dll=enforce",
            "Mods/A=B.dll=enforce",
            "UserData/Managed/**=refresh"
        ]);
    AssertEqual(DeploymentFilePolicy.Upgrade, production.Resolve("Mods/Other.dll"));
    AssertEqual(DeploymentFilePolicy.Seed, production.Resolve("Mods/Optional/Test.dll"));
    AssertEqual(DeploymentFilePolicy.Enforce, production.Resolve("Mods/Required.dll"));
    AssertEqual(DeploymentFilePolicy.Enforce, production.Resolve("Mods/A=B.dll"));
    AssertEqual(
        DeploymentFilePolicy.Refresh,
        production.Resolve("UserData/Managed/font.ab"));
    AssertEqual(DeploymentFilePolicy.Upgrade, production.Resolve("UserData/config.cfg"));
    AssertEqual(DeploymentFilePolicy.Refresh, production.Resolve("Plugins/Test.dll"));
    AssertEqual(DeploymentFilePolicy.Refresh, production.Resolve("UserLibs/Test.dll"));
    AssertEqual(DeploymentFilePolicy.Seed, production.Resolve("Custom/file.bin"));

    var locked = DeploymentPolicyOptions.Create("locked", []);
    AssertEqual(DeploymentFilePolicy.Enforce, locked.Resolve("Plugins/Test.dll"));
    AssertEqual(DeploymentFilePolicy.Enforce, locked.Resolve("Mods/Test.dll"));
    AssertEqual(DeploymentFilePolicy.Upgrade, locked.Resolve("UserData/config.cfg"));
    AssertEqual(DeploymentFilePolicy.Seed, locked.Resolve("Future/file.bin"));
    AssertThrows<ArgumentException>(() =>
        DeploymentPolicyOptions.Create("production", ["../Mods/**=refresh"]));
    AssertThrows<ArgumentException>(() =>
        DeploymentPolicyOptions.Create("production", ["Mods/*.dll=refresh"]));
    AssertThrows<ArgumentException>(() =>
        DeploymentPolicyOptions.Create("production", ["Mods//**=refresh"]));
    AssertThrows<ArgumentException>(() =>
        DeploymentPolicyOptions.Create("production", ["MelonLoader/**=enforce"]));
    AssertThrows<ArgumentException>(() =>
        DeploymentPolicyOptions.Create("production", ["Mods/**=refresh", "Mods\\**=seed"]));
    AssertThrows<InvalidDataException>(() =>
        production.ValidateRuleCoverage(["Mods/Other.dll"]));
    return Task.CompletedTask;
}

static Task TestNativeLibraryCollisionAsync()
{
    var root = CreateTestRoot();
    try
    {
        var apkPath = Path.Combine(root, "game.apk");
        CreateZip(apkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["lib/arm64-v8a/libcrypto.so"] = "game-crypto",
            ["lib/arm64-v8a/libssl.so"] = "game-ssl"
        });
        var releaseRoot = CreateReleaseTree(root, "linux-bionic-arm64");
        var interopRoot = CreateInteropTree(root);

        MergeApk(apkPath, releaseRoot, interopRoot, null);
        using (var gameApk = ZipFile.OpenRead(apkPath))
        {
            AssertTrue(
                gameApk.GetEntry("lib/arm64-v8a/libcrypto.so") is not null,
                "A game-owned public libcrypto.so was removed.");
            AssertTrue(
                gameApk.GetEntry("lib/arm64-v8a/libssl.so") is not null,
                "A game-owned public libssl.so was removed.");
            AssertTrue(
                gameApk.GetEntry("assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/11.0.0/libcrypto.so") is not null,
                "The private Bionic libcrypto.so was not packaged.");
            AssertTrue(
                gameApk.GetEntry("assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/11.0.0/libssl.so") is not null,
                "The private Bionic libssl.so was not packaged.");
        }

        var otherApkPath = Path.Combine(root, "other-game.apk");
        CreateZip(otherApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["lib/arm64-v8a/libextra.so"] = "same-content"
        });
        var otherReleaseRoot = CreateReleaseTree(root);
        WritePayload(otherReleaseRoot, "lib/arm64-v8a/libextra.so", "same-content");
        AssertThrows<InvalidDataException>(() =>
            MergeApk(otherApkPath, otherReleaseRoot, interopRoot, null));
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestDuplicateApkEntryAsync()
{
    var root = CreateTestRoot();
    try
    {
        var apkPath = Path.Combine(root, "game.apk");
        using (var archive = ZipFile.Open(apkPath, ZipArchiveMode.Create))
        {
            WriteZipEntry(archive, "lib/arm64-v8a/libmain.so", "first");
            WriteZipEntry(archive, "lib/arm64-v8a/libmain.so", "second");
        }

        AssertThrows<InvalidDataException>(() => MergeApk(
            apkPath,
            CreateReleaseTree(root),
            CreateInteropTree(root),
            null));
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static async Task TestPostProcessingContractAsync()
{
    var root = CreateTestRoot();
    try
    {
        var apk = Path.Combine(root, "game.apk");
        var output = Path.Combine(root, "patched.apk");
        File.WriteAllText(apk, "apk");
        var basic = new PatchRequest
        {
            InputPath = apk,
            OutputPath = output
        }.NormalizeAndValidate();
        AssertTrue(!basic.AlignApk, "APK alignment must be opt-in.");
        AssertTrue(basic.Signing is null, "APK signing must be opt-in.");
        await ApkPostProcessor.PublishAsync(
            apk,
            basic.OutputPath!,
            root,
            ApkPostProcessor.Resolve(basic.PostProcessing),
            null,
            CancellationToken.None);
        AssertEqual("apk", File.ReadAllText(output));

        var keystore = Path.Combine(root, "signing.jks");
        File.WriteAllText(keystore, "keystore");
        var signingOnly = new PatchRequest
        {
            InputPath = apk,
            OutputPath = output,
            Signing = new SigningOptions(keystore, "password", "alias")
        }.NormalizeAndValidate();
        AssertTrue(!signingOnly.AlignApk, "Signing must not implicitly request alignment.");
        AssertTrue(signingOnly.Signing is not null, "Signing-only APK output was rejected.");

        var missingAlignTool = new PatchRequest
        {
            InputPath = apk,
            OutputPath = Path.Combine(root, "aligned.apk"),
            AlignApk = true,
            ZipAlignPath = Path.Combine(root, "missing-zipalign")
        }.NormalizeAndValidate();
        AssertThrows<InvalidOperationException>(() =>
            ApkPostProcessor.Resolve(missingAlignTool.PostProcessing));

        var missingSignerTool = signingOnly with
        {
            OutputPath = Path.Combine(root, "signed.apk"),
            ApkSignerPath = Path.Combine(root, "missing-apksigner")
        };
        AssertThrows<InvalidOperationException>(() =>
            ApkPostProcessor.Resolve(missingSignerTool.PostProcessing));

        AssertThrows<ArgumentException>(() => new PatchRequest
        {
            InputPath = apk,
            OutputPath = output,
            ZipAlignPath = Path.Combine(root, "zipalign")
        }.NormalizeAndValidate());
        AssertThrows<ArgumentException>(() => new PatchRequest
        {
            InputPath = apk,
            OutputPath = output,
            ApkSignerPath = Path.Combine(root, "apksigner")
        }.NormalizeAndValidate());

        var directory = Path.Combine(root, "unpacked");
        Directory.CreateDirectory(directory);
        var inPlace = new PatchRequest { InputPath = directory }.NormalizeAndValidate();
        AssertTrue(inPlace.OutputPath is null, "Directory input must not create an output directory.");
        AssertThrows<ArgumentException>(() => new PatchRequest
        {
            InputPath = directory,
            OutputPath = Path.Combine(root, "copy")
        }.NormalizeAndValidate());
        AssertThrows<ArgumentException>(() => new PatchRequest
        {
            InputPath = directory,
            AlignApk = true
        }.NormalizeAndValidate());
        AssertThrows<ArgumentException>(() => new PatchRequest
        {
            InputPath = directory,
            Signing = new SigningOptions(keystore, "password", "alias")
        }.NormalizeAndValidate());
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static Task TestExternalToolResolutionAsync()
{
    var root = CreateTestRoot();
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        var explicitTool = Path.Combine(root, "explicit-tool");
        File.WriteAllText(explicitTool, "tool");
        AssertEqual(
            Path.GetFullPath(explicitTool),
            ExternalToolResolver.Resolve("zipalign", explicitTool, "--zipalign"));

        var pathToolName = OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign";
        var pathTool = Path.Combine(root, pathToolName);
        File.WriteAllText(pathTool, "tool");
        Environment.SetEnvironmentVariable("PATH", root);
        AssertEqual(
            Path.GetFullPath(pathTool),
            ExternalToolResolver.Resolve("zipalign", null, "--zipalign"));

        Environment.SetEnvironmentVariable("PATH", string.Empty);
        AssertThrows<InvalidOperationException>(() =>
            ExternalToolResolver.Resolve("apksigner", null, "--apksigner"));
        AssertThrows<InvalidOperationException>(() =>
            ExternalToolResolver.Resolve("zipalign", Path.Combine(root, "missing"), "--zipalign"));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static async Task TestCliContractAsync()
{
    var root = CreateTestRoot();
    try
    {
        var apk = Path.Combine(root, "game.apk");
        var outputApk = Path.Combine(root, "mod.apk");
        File.WriteAllText(apk, "apk");
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", apk]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", outputApk, "--unknown", "value"]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            ["--apk", apk, "--output", outputApk]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", outputApk, "--mod", "ExampleMod.dll"]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", outputApk, "--il2cppinterop-cli", "missing.dll"]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", outputApk, "--il2cppinterop-cli", typeof(ApkPatchPipeline).Assembly.Location + ".exe"]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", outputApk, "--zipalign", "zipalign"]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [apk, "--output", outputApk, "--align", "--align"]));

        var request = CliRequestParser.ParsePatchRequest(
        [
            apk,
            "--output", outputApk,
            "--deployment", root,
            "--profile", "production",
            "--il2cppinterop-cli", typeof(ApkPatchPipeline).Assembly.Location,
            "--align",
            "--policy", "Mods/**=upgrade",
            "--policy", "Mods/Required.dll=enforce"
        ]);
        AssertEqual(Path.GetFullPath(root), request.DeploymentPath);
        AssertTrue(request.AlignApk, "The --align switch was not parsed.");
        AssertEqual(
            Path.GetFullPath(typeof(ApkPatchPipeline).Assembly.Location),
            request.Il2CppInteropCliPath);
        AssertEqual(
            DeploymentFilePolicy.Upgrade,
            request.DeploymentPolicies.Resolve("Mods/Other.dll"));
        AssertEqual(
            DeploymentFilePolicy.Enforce,
            request.DeploymentPolicies.Resolve("Mods/Required.dll"));

        var directory = Path.Combine(root, "unpacked");
        Directory.CreateDirectory(directory);
        var directoryRequest = CliRequestParser.ParsePatchRequest([directory]);
        AssertTrue(directoryRequest.OutputPath is null, "CLI directory mode must be in place.");
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [directory, "--output", Path.Combine(root, "copy")]));
        AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
            [directory, "--align"]));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApplication.RunAsync(
            ["patch", "--apk", apk, "--output", outputApk],
            output,
            error);
        AssertEqual(CliApplication.UsageError, exitCode);
        AssertTrue(
            error.ToString().StartsWith("error: ", StringComparison.Ordinal),
            "CLI usage errors must use the stable error prefix.");
        AssertTrue(
            !error.ToString().Contains("   at ", StringComparison.Ordinal),
            "CLI errors must not include a stack trace unless --verbose is supplied.");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static Task TestDirectoryReplacementAsync()
{
    var root = CreateTestRoot();
    try
    {
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "new.txt"), "new");
        File.WriteAllText(Path.Combine(destination, "old.txt"), "old");
        DirectoryPublisher.Replace(source, destination);
        AssertTrue(File.Exists(Path.Combine(destination, "new.txt")), "New directory was not published.");
        AssertTrue(!File.Exists(Path.Combine(destination, "old.txt")), "Old directory content survived replacement.");
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}
