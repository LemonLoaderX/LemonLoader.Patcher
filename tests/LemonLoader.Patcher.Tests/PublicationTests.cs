using System.IO.Compression;
using System.Diagnostics;
using static TestSupport;

internal static class PublicationTests
{
    public static async Task RunAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "original.txt"), "original");
            foreach (var export in new[] { game, root, Path.Combine(game, "nested") })
                AssertThrows<ArgumentException>(() => new PatchRequest
                { InputPath = game, InteropOutputPath = export }.NormalizeAndValidate());
            var apk = Path.Combine(root, "input.apk");
            File.WriteAllText(apk, "input");
            AssertThrows<ArgumentException>(() => new PatchRequest
            { InputPath = apk, OutputPath = Path.Combine(root, "output.apk"), InteropOutputPath = root }.NormalizeAndValidate());

            var tools = Path.Combine(root, "tools");
            Directory.CreateDirectory(tools);
            var tool = Path.Combine(tools, "tool.exe");
            File.WriteAllText(tool, "input tool");
            var key = Path.Combine(root, "fixture.keystore");
            File.WriteAllText(key, "fixture");
            foreach (var export in new[] { tools, tool })
            {
                AssertThrows<ArgumentException>(() => new PatchRequest
                { InputPath = apk, OutputPath = Path.Combine(root, "output.apk"), InteropOutputPath = export,
                    AlignApk = true, ZipAlignPath = tool }.NormalizeAndValidate());
                AssertThrows<ArgumentException>(() => new PatchRequest
                { InputPath = apk, OutputPath = Path.Combine(root, "output.apk"), InteropOutputPath = export,
                    Signing = new(key, "fixture", "fixture"), ApkSignerPath = tool }.NormalizeAndValidate());
            }
            AssertEqual("input tool", File.ReadAllText(tool));
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", tools);
                foreach (var name in new[] { "zipalign", "apksigner" })
                    File.WriteAllText(Path.Combine(tools, name), "resolved tool");
                foreach (var signing in new[] { false, true })
                    await AssertThrowsAsync<ArgumentException>(() => new ApkPatchPipeline(new PatchRequest
                    {
                        InputPath = apk, OutputPath = Path.Combine(root, "output.apk"), InteropOutputPath = tools,
                        AlignApk = !signing, Signing = signing ? new(key, "fixture", "fixture") : null
                    }).RunAsync());
                AssertEqual("resolved tool", File.ReadAllText(Path.Combine(tools, "zipalign")));
                AssertEqual("resolved tool", File.ReadAllText(Path.Combine(tools, "apksigner")));
            }
            finally { Environment.SetEnvironmentVariable("PATH", previousPath); }

            var cancelledGame = Path.Combine(root, "cancelled-game");
            var cancelledOverlay = Path.Combine(root, "cancelled-overlay");
            var originalLibrary = GamePackageLayout.FilePath(cancelledGame, GamePackageLayout.MainLibrary);
            var stagedLibrary = GamePackageLayout.FilePath(cancelledOverlay, GamePackageLayout.MainLibrary);
            Directory.CreateDirectory(Path.GetDirectoryName(originalLibrary)!);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedLibrary)!);
            File.WriteAllText(originalLibrary, "original main");
            File.WriteAllText(stagedLibrary, "new main");
            Directory.CreateDirectory(Path.Combine(cancelledOverlay, "assets"));
            File.WriteAllText(Path.Combine(cancelledOverlay, "assets", "installed-before-main"), "fixture");
            using (var cancel = new CancellationTokenSource())
            {
                AssertThrows<OperationCanceledException>(() => DirectoryInjector.Apply(
                    cancelledGame, cancelledOverlay, null, cancel.Token, copyBackup: (input, backup, token) =>
                    {
                        DirectoryPublisher.CopyFile(input, backup, token);
                        cancel.Cancel();
                    }));
            }
            AssertEqual("original main", File.ReadAllText(originalLibrary));
            AssertTrue(!Directory.Exists(Path.Combine(cancelledGame, "assets")), "Cancellation did not roll back earlier files.");
            AssertTrue(!Directory.GetDirectories(cancelledGame, ".lemonloader-patcher-*").Any(), "Cancelled transaction was not cleaned.");

            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "new.txt"), "new");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "old.txt"), "old");
            AssertThrows<OperationCanceledException>(() => DirectoryPublisher.Replace(
                source, destination, cancellationToken: new CancellationToken(true)));
            AssertEqual("old", File.ReadAllText(Path.Combine(destination, "old.txt")));

            var output = Path.Combine(root, "published.apk");
            File.WriteAllText(output, "existing output");
            await AssertThrowsAsync<OperationCanceledException>(() => ApkPostProcessor.PublishAsync(
                apk, output, root, new(null, null, null), null, new CancellationToken(true)));
            AssertEqual("existing output", File.ReadAllText(output));

            File.WriteAllText(Path.Combine(destination, "a-important.txt"), "important");
            var warnings = new List<PatcherMessage>();
            DirectoryPublisher.Replace(source, destination, new InlineProgress(warnings.Add),
                deleteBackup: backup =>
                {
                    File.Delete(Path.Combine(backup, "a-important.txt"));
                    throw new IOException("Injected partial backup cleanup failure.");
                });
            AssertEqual("new", File.ReadAllText(Path.Combine(destination, "new.txt")));
            AssertTrue(warnings.Any(x => x.Kind == PatcherMessageKind.Warning), "Backup cleanup failure was not reported.");

            if (OperatingSystem.IsWindows())
            {

                var linkedGame = Path.Combine(root, "linked-game");
                var outside = Path.Combine(root, "outside");
                var overlay = Path.Combine(root, "overlay");
                Directory.CreateDirectory(linkedGame); Directory.CreateDirectory(outside);
                Directory.CreateDirectory(Path.Combine(overlay, "assets"));
                File.WriteAllText(Path.Combine(overlay, "assets", "probe.txt"), "fixture");
                var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoProfile", "-Command", $"New-Item -ItemType Junction -Path '{Path.Combine(linkedGame, "assets")}' -Target '{outside}' | Out-Null" })
                    info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                await process.WaitForExitAsync();
                AssertEqual(0, process.ExitCode);
                try
                {
                    AssertThrows<InvalidDataException>(() => DirectoryInjector.Apply(linkedGame, overlay, null));
                    AssertTrue(!File.Exists(Path.Combine(outside, "probe.txt")), "Injection traversed a junction.");
                }
                finally { Directory.Delete(Path.Combine(linkedGame, "assets")); }
            }

            foreach (var name in new[] { "MelonLoader.dll", "MelonLoader.NativeHost.dll" })
            {
                var release = CreateReleaseTree(root);
                File.Delete(Path.Combine(release, $"{AndroidPayloadContract.LoaderRoot}/net6/{name}"));
                RefreshReleaseInventory(release);
                AssertThrows<InvalidDataException>(() => ReleaseValidator.Validate(release));
            }

            var validRelease = CreateReleaseTree(root);
            var interop = CreateInteropTree(root);
            foreach (var unsafeName in new[] { "../escape.txt", "/absolute", "assets/../escape", "assets\\escape", "C:/escape", "assets//escape" })
            {
                var input = Path.Combine(root, Guid.NewGuid() + ".apk");
                CreateZip(input, new Dictionary<string, string> { [unsafeName] = "unsafe", [GamePackageLayout.MainLibrary] = "main" });
                AssertThrows<InvalidDataException>(() => MergeApk(input, validRelease, interop, null));
            }
            using (var stream = new MemoryStream())
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                archive.CreateEntry("assets/"); archive.CreateEntry("assets/valid");
                ArchiveSafety.Validate(archive);
            }

            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(cache);
            var cached = Path.Combine(cache, RuntimeVariants.ArchiveName("android"));
            ZipFile.CreateFromDirectory(validRelease, cached);
            using (var zip = ZipFile.Open(cached, ZipArchiveMode.Update))
            {
                zip.GetEntry(GamePackageLayout.MainLibrary)!.Delete();
                WriteZipEntry(zip, GamePackageLayout.MainLibrary, "damaged");
            }
            using var archiveBytes = new MemoryStream();
            using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Create, true))
                foreach (var file in Directory.GetFiles(validRelease, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(validRelease, file).Replace('\\', '/'));
            var handler = new ReleaseHandler(archiveBytes.ToArray());
            using var client = new HttpClient(handler);
            AssertEqual(cached, await ReleaseResolver.ResolveLatestAsync(cache, client, null, CancellationToken.None, "android"));
            AssertEqual(1, handler.Requests);
            var extracted = Path.Combine(root, "resolved-release");
            AssertEqual(cached, await ReleaseResolver.ResolveLatestAsync(cache, client, null,
                CancellationToken.None, "android", extracted));
            AssertTrue(File.Exists(Path.Combine(extracted, AndroidPayloadContract.PayloadManifestPath)),
                "Validated cache extraction was not retained for the patch pipeline.");
            ReleaseValidator.Validate(extracted);
            AssertEqual(cached, await ReleaseResolver.ResolveLatestAsync(cache, client, null, CancellationToken.None, "android"));
            AssertEqual(1, handler.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class InlineProgress(Action<PatcherMessage> report) : IProgress<PatcherMessage>
    { public void Report(PatcherMessage value) => report(value); }

    private sealed class ReleaseHandler(byte[] archive) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { ++Requests; return Task.FromResult(ZipResponse(archive)); }
    }
}
