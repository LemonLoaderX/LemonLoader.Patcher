using System.IO.Compression;
using static TestSupport;

internal static class WorkflowTests
{
    internal static async Task RunAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var game = Path.Combine(root, "original.apk");
            CreateZip(game, new Dictionary<string, string>
            {
                [GamePackageLayout.MainLibrary] = "original main", [GamePackageLayout.UnityLibrary] = "original unity",
                [GamePackageLayout.Il2CppLibrary] = "binary", [GamePackageLayout.Metadata] = "metadata", ["classes.dex"] = "original dex"
            });
            var interop = Path.Combine(root, "third-party-interop");
            Directory.CreateDirectory(interop);
            File.Copy(typeof(ApkPatchPipeline).Assembly.Location, Path.Combine(interop, "Game.dll"));
            File.WriteAllText(Path.Combine(interop, "interop-manifest.json"), "unrelated invalid JSON");
            var release = Path.Combine(root, "Loader.zip");
            ZipFile.CreateFromDirectory(CreateReleaseTree(root), release);
            var output = Path.Combine(root, "injected.apk");
            var lines = new List<PatcherMessage>();
            var progress = new InlineProgress(lines.Add);
            var request = new PatchRequest { InputPath = game, OutputPath = output, ReleasePath = release, InteropInputPath = interop };
            var result = await new ApkPatchPipeline(request, progress).RunAsync();
            AssertTrue(result.UnityVersion is null, "Injection tried to detect an unnecessary Unity version.");
            AssertTrue(!lines.Any(line => line.Text.Contains("Running ") || line.Text.Contains("Downloading") || line.Text.Contains("Generating Interop")),
                "Injection invoked generation/download tools.");
            using (var apk = ZipFile.OpenRead(output))
            {
                AssertEqual("original dex", ReadZipEntry(apk, "classes.dex"));
                AssertEqual("loader-main", ReadZipEntry(apk, GamePackageLayout.MainLibrary));
                AssertTrue(apk.GetEntry(AndroidPayloadContract.InteropRoot + "/Game.dll") is not null, "Supplied DLL missing.");
                AssertTrue(apk.GetEntry(AndroidPayloadContract.InteropRoot + "/interop-manifest.json") is null, "Injection consumed a host manifest.");
            }
            var directory = Path.Combine(root, "unpacked");
            ZipFile.ExtractToDirectory(game, directory);
            var directoryResult = await new ApkPatchPipeline(request with { InputPath = directory, OutputPath = null }).RunAsync();
            AssertTrue(directoryResult.ModifiedInPlace, "Directory injection did not complete.");
            AssertEqual("original unity", File.ReadAllText(GamePackageLayout.FilePath(directory, GamePackageLayout.UnityLibrary)));
            AssertThrows<ArgumentException>(() => (request with { UnityVersion = "2022.3.62f2" }).NormalizeAndValidate());
            AssertThrows<ArgumentException>(() => (request with { InteropOutputPath = Path.Combine(root, "export") }).NormalizeAndValidate());
            AssertThrows<ArgumentException>(() => (request with { OutputPath = Path.Combine(interop, "Game.dll") }).NormalizeAndValidate());
            AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest([game, "--output", output], injectionOnly: true));
            AssertThrows<CliUsageException>(() => CliRequestParser.ParsePatchRequest(
                [game, "--output", output, "--interop", interop, "--cpp2il", "unused"], injectionOnly: true));
            using var text = new StringWriter();
            using var errors = new StringWriter();
            AssertEqual(0, await CliApplication.RunAsync(["inject", game, "--output", output, "--release", release, "--interop", interop], text, errors));
            AssertTrue(!text.ToString().Contains("unity-version:"), "Injection reports a fabricated Unity version.");

            File.WriteAllText(output, "previous output");
            await AssertThrowsAsync<OperationCanceledException>(() => new ApkPatchPipeline(request).RunAsync(new CancellationToken(true)));
            AssertEqual("previous output", File.ReadAllText(output));

            var standalone = new InteropRequest
            {
                GameAssemblyPath = GamePackageLayout.FilePath(directory, GamePackageLayout.Il2CppLibrary),
                MetadataPath = GamePackageLayout.FilePath(directory, GamePackageLayout.Metadata), UnityVersion = "2022.3.62f2"
            }.NormalizeAndValidate();
            var work = Path.Combine(root, "raw-inputs"); Directory.CreateDirectory(work);
            AssertEqual("2022.3.62f2", await new GameInteropGenerator(standalone, null).ExtractInputsAsync(work, default));
            AssertThrows<ArgumentException>(() => (standalone with { UnityVersion = null }).NormalizeAndValidate());
            AssertThrows<ArgumentException>(() => new InteropPipeline(standalone));
            AssertThrows<ArgumentException>(() => (standalone with { OutputPath = directory }).NormalizeAndValidate());
            AssertThrows<ArgumentException>(() => (standalone with { OutputPath = AppContext.BaseDirectory }).NormalizeAndValidate());
            AssertThrows<ArgumentException>(() => (standalone with { OutputPath = Path.Combine(AppContext.BaseDirectory, "Tools", "Il2CppInterop") }).NormalizeAndValidate());
            var parsed = CliRequestParser.ParseInteropRequest(["--output", Path.Combine(root, "generated"),
                "--game-assembly", standalone.GameAssemblyPath!, "--metadata", standalone.MetadataPath!, "--unity-version", standalone.UnityVersion!]);
            AssertTrue(parsed.InputPath is null && parsed.OutputPath is not null, "Raw generation CLI requires a game package.");
            AssertThrows<CliUsageException>(() => CliRequestParser.ParseInteropRequest([game, "--output", output, "--release", release]));
            AssertThrows<ArgumentException>(() => new ApkProcessingPipeline(new() { InputPath = game, OutputPath = output }));
            AssertThrows<CliUsageException>(() => CliRequestParser.ParseProcessingRequest([game, "--output", output, "--interop", interop]));
            foreach (var command in new[] { "generate-interop", "inject", "process-apk" })
            {
                text.GetStringBuilder().Clear();
                AssertEqual(0, await CliApplication.RunAsync([command, "--help"], text, errors));
                AssertTrue(text.ToString().Contains(command), "Missing stage help.");
            }
            AssertTrue(!typeof(ApkPatchPipeline).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == "Mono.Cecil"),
                "Retired binary rewriting dependency remains.");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class InlineProgress(Action<PatcherMessage> report) : IProgress<PatcherMessage>
    { public void Report(PatcherMessage value) => report(value); }
}
