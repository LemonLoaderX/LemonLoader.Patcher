using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static TestSupport;

internal static class InteropCacheTests
{
    internal static Task RunAsync()
    {
        var root = CreateTestRoot();
        try
        {
            var cache = Path.Combine(root, "cache");
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(output);
            var inputs = Path.Combine(root, "inputs");
            Directory.CreateDirectory(inputs);
            File.WriteAllText(Path.Combine(inputs, "libil2cpp.so"), "binary-one");
            File.WriteAllText(Path.Combine(inputs, "global-metadata.dat"), "metadata-one");
            var cpp = Path.Combine(root, "cpp.exe");
            File.WriteAllText(cpp, "cpp-one");
            var unity = new UnityDependenciesResolution(root, "2021.3.0f1", "2021.3.0", "test", null, null, 1, "unity-one");
            var tool = new InteropGeneratorTool("unused.dll", "1.0", "bundled-fork", "dll-one", "tool-one");
            string Key() => InteropGenerationCache.Key(inputs, "2021.3.0f1", unity, cpp, tool);
            var originalKey = Key();
            if (originalKey != Key()) throw new Exception("Cache identity is unstable.");
            File.WriteAllText(Path.Combine(inputs, "libil2cpp.so"), "binary-two");
            if (originalKey == Key()) throw new Exception("Binary change did not invalidate cache.");
            File.WriteAllText(Path.Combine(inputs, "libil2cpp.so"), "binary-one");
            File.WriteAllText(Path.Combine(inputs, "global-metadata.dat"), "metadata-two");
            if (originalKey == Key()) throw new Exception("Metadata change did not invalidate cache.");
            File.WriteAllText(Path.Combine(inputs, "global-metadata.dat"), "metadata-one");
            foreach (var changed in new[] {
                InteropGenerationCache.Key(inputs, "2022.1.0f1", unity, cpp, tool),
                InteropGenerationCache.Key(inputs, "2021.3.0f1", unity with { ContentSha256 = "unity-two" }, cpp, tool),
                InteropGenerationCache.Key(inputs, "2021.3.0f1", unity, cpp, tool with { ContentSha256 = "tool-two" }),
                InteropGenerationCache.Key(inputs, "2021.3.0f1", unity, cpp, tool with { Source = "override" }) })
                if (originalKey == changed) throw new Exception("Dependency/generation contract change did not invalidate cache.");
            File.WriteAllText(cpp, "cpp-two");
            if (originalKey == Key()) throw new Exception("Cpp2IL change did not invalidate cache.");
            File.WriteAllText(Path.Combine(cache, "Wrapper.dll"), "original-wrapper");
            var manifest = new JsonObject
            {
                ["formatVersion"] = 1, ["cacheKey"] = "expected",
                ["assemblies"] = new JsonArray(new JsonObject
                {
                    ["name"] = "Wrapper.dll", ["size"] = 16,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData("original-wrapper"u8)).ToLowerInvariant()
                })
            };
            void Save() => File.WriteAllText(Path.Combine(cache, InteropGenerationManifest.FileName), manifest.ToJsonString());
            bool Restore(string key = "expected") => InteropGenerationCache.TryRestore(cache, key, output, default);
            Save();
            if (!Restore() || File.ReadAllText(Path.Combine(output, "Wrapper.dll")) != "original-wrapper")
                throw new Exception("Valid cache was not restored.");
            if (Restore("different-tool-or-game")) throw new Exception("Wrong cache identity was accepted.");
            File.WriteAllText(Path.Combine(cache, "Wrapper.dll"), "modified-wrapper");
            if (Restore()) throw new Exception("Same-length modified cache was accepted.");
            File.WriteAllText(Path.Combine(cache, "Wrapper.dll"), "original-wrapper");
            File.WriteAllText(Path.Combine(cache, "Extra.dll"), "extra");
            if (Restore()) throw new Exception("Unlisted assembly was accepted.");
            File.Delete(Path.Combine(cache, "Extra.dll"));
            manifest["assemblies"]![0]!["name"] = "../Wrapper.dll";
            Save();
            if (Restore()) throw new Exception("Unsafe cache filename was accepted.");
            File.WriteAllText(Path.Combine(cache, InteropGenerationManifest.FileName), "invalid json");
            if (Restore() || Directory.EnumerateFiles(output).Any()) throw new Exception("Invalid/partial result survived.");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            manifest["assemblies"]![0]!["name"] = "Wrapper.dll";
            Save();
            try { InteropGenerationCache.TryRestore(cache, "expected", output, cancellation.Token); }
            catch (OperationCanceledException) { return Task.CompletedTask; }
            throw new Exception("Cache restore ignored cancellation.");
        }
        finally { Directory.Delete(root, true); }
    }
}
