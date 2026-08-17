using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Unity version normalization", TestUnityVersionNormalizationAsync),
    ("Unity dependency cache repair", TestUnityDependencyCacheRepairAsync),
    ("Unity dependency source fallback", TestUnityDependencySourceFallbackAsync),
    ("Interop generator game assembly", TestInteropGeneratorGameAssemblyAsync),
    ("Release payload hash validation", TestReleaseValidationAsync),
    ("APK payload layout", TestApkPayloadLayoutAsync),
    ("Deployment policy resolution", TestDeploymentPolicyResolutionAsync),
    ("Native library collision rejection", TestNativeLibraryCollisionAsync),
    ("Duplicate APK entry rejection", TestDuplicateApkEntryAsync),
    ("CLI contract", TestCliContractAsync),
    ("Directory replacement", TestDirectoryReplacementAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

Console.WriteLine($"LemonLoader.Patcher tests passed: {tests.Length}");
return;

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
    var arguments = ApkPatchPipeline.BuildInteropGeneratorArguments(
        "Il2CppInterop.CLI.dll",
        input,
        Path.Combine("root", "cpp2il"),
        Path.Combine("root", "output"),
        Path.Combine("root", "unity"));
    var optionIndex = Array.IndexOf(arguments, "--game-assembly");
    AssertTrue(optionIndex >= 0, "Interop generation must receive the game assembly.");
    AssertEqual(Path.Combine(input, "libil2cpp.so"), arguments[optionIndex + 1]);
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
        var files = new List<(string Path, long Size, string Hash)>
        {
            WritePayload(root, "lib/arm64-v8a/libmain.so", "bootstrap"),
            WritePayload(root, "assets/LemonLoader/runtime/dotnet/native/openssl/lemcrypto.so", "crypto"),
            WritePayload(root, "assets/LemonLoader/runtime/dotnet/native/openssl/lemssl.so", "ssl")
        };
        files.Add(WritePayload(
            root,
            "assets/LemonLoader/payload.json",
            JsonSerializer.Serialize(new
            {
                formatVersion = AndroidPayloadContract.FormatVersion,
                loaderSha256 = ComputePayloadDirectoryHash(root, "runtime/loader"),
                dotnetSha256 = ComputePayloadDirectoryHash(root, "runtime/dotnet"),
                interopSha256 = ComputePayloadDirectoryHash(root, "runtime/interop"),
                deploymentSha256 = ComputePayloadDirectoryHash(root, "deployment"),
                deploymentProfile = "development",
                deploymentRevisionSha256 = ComputeDeploymentRevision([]),
                deploymentFiles = Array.Empty<object>(),
                privateNativeLibraries = new[] { "lemcrypto.so", "lemssl.so" }
            })));
        var manifest = new
        {
            formatVersion = 1,
            assetLayoutVersion = AndroidPayloadContract.FormatVersion,
            gameAssembliesIncluded = false,
            files = files.Select(file => new
            {
                path = file.Path,
                size = file.Size,
                sha256 = file.Hash
            })
        };
        File.WriteAllText(
            Path.Combine(root, "lemonloader-release.json"),
            JsonSerializer.Serialize(manifest));
        ReleaseValidator.Validate(root);

        var documentation = WritePayload(
            root,
            "assets/LemonLoader/runtime/loader/Documentation/README.md",
            "not for Android");
        AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(root));
        File.Delete(Path.Combine(
            root,
            documentation.Path.Replace('/', Path.DirectorySeparatorChar)));

        File.AppendAllText(Path.Combine(root, "lib", "arm64-v8a", "libmain.so"), "corrupt");
        AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(root));
        File.WriteAllText(Path.Combine(root, "lib", "arm64-v8a", "libmain.so"), "bootstrap", Encoding.UTF8);
        WritePayload(root, "assets/unexpected.dll", "unexpected");
        AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(root));
    }
    finally
    {
        Directory.Delete(root, true);
    }
    return Task.CompletedTask;
}

static Task TestApkPayloadLayoutAsync()
{
    var root = CreateTestRoot();
    try
    {
        var apkPath = Path.Combine(root, "game.apk");
        CreateZip(apkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main"
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

        ApkPatchPipeline.MergeZip(apkPath, releaseRoot, interopRoot, deploymentRoot,
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
        using var payloadDocument = JsonDocument.Parse(
            ReadZipEntry(archive, "assets/LemonLoader/payload.json"));
        AssertEqual(
            ComputePayloadHash(archive, "runtime/loader"),
            payloadDocument.RootElement.GetProperty("loaderSha256").GetString());
        AssertEqual(
            ComputePayloadHash(archive, "runtime/dotnet"),
            payloadDocument.RootElement.GetProperty("dotnetSha256").GetString());
        AssertEqual(
            ComputePayloadHash(archive, "runtime/interop"),
            payloadDocument.RootElement.GetProperty("interopSha256").GetString());
        AssertTrue(
            !payloadDocument.RootElement.TryGetProperty("runtimeSha256", out _) &&
            !payloadDocument.RootElement.TryGetProperty("runtimeFiles", out _),
            "The APK retained the obsolete aggregate runtime manifest.");
        AssertTrue(
            archive.Entries.All(entry => !AndroidPayloadContract.IsForbiddenReleasePath(entry.FullName)),
            "Loader Documentation leaked into the APK.");
        AssertEqual(
            ComputePayloadHash(archive, "deployment"),
            payloadDocument.RootElement.GetProperty("deploymentSha256").GetString());
        AssertEqual(
            "production",
            payloadDocument.RootElement.GetProperty("deploymentProfile").GetString());
        var deploymentFileElements = payloadDocument.RootElement
            .GetProperty("deploymentFiles")
            .EnumerateArray()
            .ToArray();
        var deploymentFiles = deploymentFileElements
            .ToDictionary(
                item => item.GetProperty("path").GetString()!,
                item => item.GetProperty("policy").GetString()!,
                StringComparer.Ordinal);
        AssertEqual("refresh", deploymentFiles["Mods/ExampleMod.dll"]);
        AssertEqual("refresh", deploymentFiles["Plugins/ExamplePlugin.dll"]);
        AssertEqual("refresh", deploymentFiles["UserLibs/SharedLibrary.dll"]);
        AssertEqual("enforce", deploymentFiles["UserData/Fonts/font.ab"]);
        AssertEqual("seed", deploymentFiles["Custom/fixture.bin"]);
        AssertEqual(
            ComputeDeploymentRevision(deploymentFileElements),
            payloadDocument.RootElement.GetProperty("deploymentRevisionSha256").GetString());
        foreach (var file in deploymentFileElements)
        {
            var path = file.GetProperty("path").GetString()!;
            var entry = archive.GetEntry($"assets/LemonLoader/deployment/{path}")!;
            using var input = entry.Open();
            AssertEqual(entry.Length, file.GetProperty("size").GetInt64());
            AssertEqual(
                Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(),
                file.GetProperty("sha256").GetString());
        }

        var alreadyPatchedApkPath = Path.Combine(root, "already-patched.apk");
        CreateZip(alreadyPatchedApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["assets/LemonLoader/payload.json"] = "existing-loader"
        });
        AssertThrows<InvalidDataException>(() => ApkPatchPipeline.MergeZip(
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
        AssertThrows<InvalidDataException>(() => ApkPatchPipeline.MergeZip(
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
        AssertThrows<InvalidDataException>(() => ApkPatchPipeline.MergeZip(
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
        AssertThrows<InvalidDataException>(() => ApkPatchPipeline.MergeZip(
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
        AssertThrows<InvalidDataException>(() => ApkPatchPipeline.MergeZip(
            documentationApkPath,
            documentationRelease,
            interopRoot,
            null));
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
        var releaseRoot = CreateReleaseTree(root, ["lemcrypto.so", "lemssl.so"]);
        var interopRoot = CreateInteropTree(root);

        ApkPatchPipeline.MergeZip(apkPath, releaseRoot, interopRoot, null);
        using (var gameApk = ZipFile.OpenRead(apkPath))
        {
            AssertTrue(
                gameApk.GetEntry("lib/arm64-v8a/libcrypto.so") is not null,
                "A game-owned public libcrypto.so was removed.");
            AssertTrue(
                gameApk.GetEntry("lib/arm64-v8a/libssl.so") is not null,
                "A game-owned public libssl.so was removed.");
            AssertTrue(
                gameApk.GetEntry("assets/LemonLoader/runtime/dotnet/native/openssl/lemcrypto.so") is not null,
                "The isolated private lemcrypto.so was not packaged.");
            AssertTrue(
                gameApk.GetEntry("assets/LemonLoader/runtime/dotnet/native/openssl/lemssl.so") is not null,
                "The isolated private lemssl.so was not packaged.");
        }

        var privateCollisionApkPath = Path.Combine(root, "private-collision.apk");
        CreateZip(privateCollisionApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["lib/arm64-v8a/lemssl.so"] = "conflicting-private-name"
        });
        AssertThrows<InvalidDataException>(() =>
            ApkPatchPipeline.MergeZip(privateCollisionApkPath, releaseRoot, interopRoot, null));

        var otherApkPath = Path.Combine(root, "other-game.apk");
        CreateZip(otherApkPath, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lib/arm64-v8a/libmain.so"] = "game-main",
            ["lib/arm64-v8a/libextra.so"] = "same-content"
        });
        var otherReleaseRoot = CreateReleaseTree(root);
        WritePayload(otherReleaseRoot, "lib/arm64-v8a/libextra.so", "same-content");
        AssertThrows<InvalidDataException>(() =>
            ApkPatchPipeline.MergeZip(otherApkPath, otherReleaseRoot, interopRoot, null));
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

        AssertThrows<InvalidDataException>(() => ApkPatchPipeline.MergeZip(
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

static async Task TestCliContractAsync()
{
    var apk = Path.GetFullPath("game.apk");
    AssertThrows<CliUsageException>(() => CliApplication.ParsePatchRequest(
        [apk, "--output", apk]));
    AssertThrows<CliUsageException>(() => CliApplication.ParsePatchRequest(
        [apk, "--output", Path.GetFullPath("mod.apk"), "--unknown", "value"]));
    AssertThrows<CliUsageException>(() => CliApplication.ParsePatchRequest(
        ["--apk", apk, "--output", Path.GetFullPath("mod.apk")]));
    AssertThrows<CliUsageException>(() => CliApplication.ParsePatchRequest(
        [apk, "--output", Path.GetFullPath("mod.apk"), "--mod", "ExampleMod.dll"]));
    var request = CliApplication.ParsePatchRequest(
    [
        apk,
        "--output", Path.GetFullPath("mod.apk"),
        "--deployment", ".",
        "--profile", "production",
        "--policy", "Mods/**=upgrade",
        "--policy", "Mods/Required.dll=enforce"
    ]);
    AssertEqual(Path.GetFullPath("."), request.DeploymentPath);
    AssertEqual(
        DeploymentFilePolicy.Upgrade,
        request.DeploymentPolicies.Resolve("Mods/Other.dll"));
    AssertEqual(
        DeploymentFilePolicy.Enforce,
        request.DeploymentPolicies.Resolve("Mods/Required.dll"));
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exitCode = await CliApplication.RunAsync(
        ["patch", "--apk", apk, "--output", Path.GetFullPath("mod.apk")],
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

static byte[] CreateUnityArchive()
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

static string CreateReleaseTree(
    string root,
    IReadOnlyList<string>? privateNativeLibraries = null)
{
    var releaseRoot = Path.Combine(root, $"release-{Guid.NewGuid():N}");
    WritePayload(releaseRoot, "lib/arm64-v8a/libmain.so", "loader-main");
    WritePayload(releaseRoot, "assets/LemonLoader/runtime/loader/net6/MelonLoader.dll", "loader");
    WritePayload(releaseRoot, "assets/LemonLoader/runtime/dotnet/host/fxr/10.0.10/libhostfxr.so", "hostfxr");
    foreach (var library in privateNativeLibraries ?? [])
        WritePayload(
            releaseRoot,
            $"assets/LemonLoader/runtime/dotnet/native/openssl/{library}",
            $"private-{library}");
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
            deploymentProfile = "development",
            deploymentRevisionSha256 = ComputeDeploymentRevision([]),
            deploymentFiles = Array.Empty<object>(),
            privateNativeLibraries = privateNativeLibraries ?? []
        }));
    return releaseRoot;
}

static string CreateInteropTree(string root)
{
    var interopRoot = Path.Combine(root, $"interop-{Guid.NewGuid():N}");
    Directory.CreateDirectory(interopRoot);
    File.WriteAllText(Path.Combine(interopRoot, "Game.dll"), "interop");
    File.WriteAllText(
        Path.Combine(interopRoot, InteropGenerationManifest.FileName),
        "{}");
    return interopRoot;
}

static void CreateZip(string path, IReadOnlyDictionary<string, string> entries)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries)
        WriteZipEntry(archive, entry.Key, entry.Value);
}

static void WriteZipEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name);
    using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
    writer.Write(content);
}

static string ReadZipEntry(ZipArchive archive, string name)
{
    using var reader = new StreamReader(
        archive.GetEntry(name)?.Open() ?? throw new InvalidOperationException($"Missing ZIP entry '{name}'."),
        Encoding.UTF8);
    return reader.ReadToEnd();
}

static string ComputePayloadHash(ZipArchive archive, string scope)
{
    var lines = new List<string>
    {
        $"layout-version={AndroidPayloadContract.FormatVersion}",
        $"scope={scope}"
    };
    foreach (var entry in archive.Entries
                 .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
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
    return Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
}

static string ComputePayloadDirectoryHash(string releaseRoot, string scope)
{
    var payloadRoot = Path.Combine(releaseRoot, "assets", "LemonLoader");
    var scopeRoot = Path.Combine(payloadRoot, scope);
    var files = Directory.Exists(scopeRoot)
        ? Directory.EnumerateFiles(scopeRoot, "*", SearchOption.AllDirectories)
        : Enumerable.Empty<string>();
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
            $"{relativePath}|{input.Length}|{Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()}");
    }
    return Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
}

static string ComputeDeploymentRevision(IEnumerable<JsonElement> files)
{
    var lines = new List<string> { "deployment-revision=1" };
    lines.AddRange(files
        .OrderBy(file => file.GetProperty("path").GetString(), StringComparer.Ordinal)
        .Select(file =>
            $"{file.GetProperty("path").GetString()}|" +
            $"{file.GetProperty("size").GetInt64()}|" +
            $"{file.GetProperty("sha256").GetString()}|" +
            file.GetProperty("policy").GetString()));
    return Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
}

static HttpResponseMessage ZipResponse(byte[] archive) => new(HttpStatusCode.OK)
{
    Content = new ByteArrayContent(archive)
};

static (string Path, long Size, string Hash) WritePayload(
    string root,
    string relativePath,
    string content)
{
    var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, Encoding.UTF8);
    using var input = File.OpenRead(path);
    return (
        relativePath,
        input.Length,
        Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant());
}

static string CreateTestRoot()
{
    var root = Path.Combine(Path.GetTempPath(), $"lemonloader-patcher-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void AssertThrows<TException>(Action action) where TException : Exception
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

static async Task AssertThrowsAsync<TException>(Func<Task> action)
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

internal sealed class DelegateHandler(
    Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(handler(request));
}
