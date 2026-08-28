using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static TestSupport;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Unity version normalization", TestUnityVersionNormalizationAsync),
    ("Unity dependency cache repair", TestUnityDependencyCacheRepairAsync),
    ("Unity dependency source fallback", TestUnityDependencySourceFallbackAsync),
    ("Interop generator game assembly", TestInteropGeneratorGameAssemblyAsync),
    ("Interop generator override provenance", TestInteropGeneratorOverrideAsync),
    ("Release payload hash validation", TestReleaseValidationAsync),
    ("APK payload layout", TestApkPayloadLayoutAsync),
    ("Directory payload injection", TestDirectoryPayloadInjectionAsync),
    ("Deployment policy resolution", TestDeploymentPolicyResolutionAsync),
    ("Native library collision rejection", TestNativeLibraryCollisionAsync),
    ("Duplicate APK entry rejection", TestDuplicateApkEntryAsync),
    ("Signing password isolation", TestSigningPasswordIsolationAsync),
    ("Optional APK post-processing contract", TestPostProcessingContractAsync),
    ("External Android tool resolution", TestExternalToolResolutionAsync),
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
        AssertEqual(
            "aecf17eeb5a6edd0b1aa4d1dc6460a83a0716aba",
            BundledInteropGeneratorTool.Revision);
        AssertEqual(
            "https://github.com/anosu/LemonLoader/releases/latest/download/LemonLoader-Android-arm64.zip",
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
        var files = new List<(string Path, long Size, string Hash)>
        {
            WritePayload(root, "lib/arm64-v8a/libmain.so", "bootstrap"),
            WritePayload(root, "assets/LemonLoader/runtime/dotnet/native/openssl/lemcrypto.so", "crypto"),
            WritePayload(root, "assets/LemonLoader/runtime/dotnet/native/openssl/lemssl.so", "ssl"),
            WritePayload(
                root,
                "assets/LemonLoader/runtime/dotnet/shared/Microsoft.NETCore.App/10.0.10/libcoreclr.so",
                "coreclr")
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
        var coreClrHash = files.Single(file => file.Path.EndsWith("/libcoreclr.so")).Hash;
        var manifest = new Dictionary<string, object>
        {
            ["formatVersion"] = 1,
            ["assetLayoutVersion"] = AndroidPayloadContract.FormatVersion,
            ["gameAssembliesIncluded"] = false,
            ["dotnetRuntimeVersion"] = "10.0.10",
            ["dotnetRuntimeRevision"] = new string('1', 40),
            ["coreClrSha256"] = coreClrHash,
            ["files"] = files.Select(file => new
            {
                path = file.Path,
                size = file.Size,
                sha256 = file.Hash
            })
        };
        var manifestPath = Path.Combine(root, "lemonloader-release.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        ReleaseValidator.Validate(root);

        manifest["coreClrSha256"] = new string('0', 64);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(root));
        manifest["coreClrSha256"] = coreClrHash;

        manifest["dotnetRuntimeRevision"] = "not-a-source-revision";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(root));
        manifest["dotnetRuntimeRevision"] = new string('1', 40);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

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
        AssertThrows<InvalidDataException>(() => MergeApk(
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

static Task TestDirectoryPayloadInjectionAsync()
{
    var root = CreateTestRoot();
    try
    {
        var gameRoot = Path.Combine(root, "unpacked-game");
        WritePayload(gameRoot, "lib/arm64-v8a/libmain.so", "game-main");
        WritePayload(gameRoot, "lib/arm64-v8a/libunity.so", "unity");
        WritePayload(gameRoot, "lib/arm64-v8a/libil2cpp.so", "il2cpp");
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
        AssertTrue(
            Directory.GetDirectories(gameRoot, ".lemonloader-patcher-*", SearchOption.TopDirectoryOnly).Length == 0,
            "Directory injection left a transaction directory behind.");

        using var payload = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            gameRoot,
            "assets",
            "LemonLoader",
            "payload.json")));
        AssertEqual(
            AndroidPayloadContract.ComputeTreeHash(gameRoot, "runtime/interop"),
            payload.RootElement.GetProperty("interopSha256").GetString());
        AssertEqual("production", payload.RootElement.GetProperty("deploymentProfile").GetString());

        AssertThrows<InvalidDataException>(() => InjectDirectory(
            gameRoot,
            releaseRoot,
            interopRoot,
            null));

        var collisionRoot = Path.Combine(root, "native-collision");
        WritePayload(collisionRoot, "lib/arm64-v8a/libmain.so", "original-main");
        WritePayload(collisionRoot, "lib/arm64-v8a/libunity.so", "unity");
        WritePayload(collisionRoot, "lib/arm64-v8a/lemssl.so", "game-private-name");
        AssertThrows<InvalidDataException>(() => InjectDirectory(
            collisionRoot,
            CreateReleaseTree(root, ["lemssl.so"]),
            interopRoot,
            null));
        AssertEqual("original-main", File.ReadAllText(
            Path.Combine(collisionRoot, "lib", "arm64-v8a", "libmain.so"),
            Encoding.UTF8));
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
            MergeApk(privateCollisionApkPath, releaseRoot, interopRoot, null));

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
