using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using static TestSupport;

internal static class UnityVersionDetectionTests
{
    internal static async Task RunAsync()
    {
        foreach (var format in new uint[] { 9, 17, 21, 22 })
            foreach (var endian in new byte[] { 0, 1 })
            {
                var bytes = Serialized("2022.3.62f2", format, endian);
                Encoding.ASCII.GetBytes("2021.3.25f1\0").CopyTo(bytes, 512);
                AssertHeader("2022.3.62f2", bytes, false, format == 22 ? 60 : 32);
            }
        foreach (var format in new uint[] { 6, 7, 8 })
            AssertHeader("2022.3.62f2", Bundle("2022.3.62f2", format), true, 30);
        AssertHeader("2022.3.62f2", Bundle("2022.3.62f2", 6, "UnityWeb"), true, 31);
        AssertHeader("2022.3.62f2", Bundle("2022.3.62f2", 6, "UnityRaw"), true, 31);
        AssertHeader("2022.3.62f2", Bundle("2022.3.62f2", player: "2020.3.0f1"), true, 35);

        var large = Serialized("6000.3.8f1", 22);
        BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(24), (ulong)uint.MaxValue + 4096);
        AssertHeader("6000.3.8f1", large, false, 59);

        var invalid = new List<(byte[] Bytes, bool Bundle)>
        {
            ([], false), (Serialized("2022.3.62f2")[..25], false),
            (Serialized("2022.3.62f2", 23), false),
            (Serialized("2022.3.62f2", 17, 2), false),
            (Serialized(new string('1', 70)), false),
            (Bundle("2022.3.62f2", 9), true), (Bundle("2022.3.62f2", 5), true),
            (Bundle("2022.3.62f2", signature: "UnityArchive"), true),
            (Bundle("2022.3.62f2")[..25], true),
            (Bundle(new string('1', 70)), true)
        };
        var shortMetadata = Serialized("2022.3.62f2");
        BinaryPrimitives.WriteUInt32BigEndian(shortMetadata, 3);
        invalid.Add((shortMetadata, false));
        var invalidOffset = Serialized("2022.3.62f2");
        BinaryPrimitives.WriteUInt32BigEndian(invalidOffset.AsSpan(12), 10);
        invalid.Add((invalidOffset, false));
        foreach (var value in new[] { "0.0.0", "", "5.x.x", "prefix2022.3.62f2", "2022.3.62f2junk" })
        {
            invalid.Add((Serialized(value), false));
            invalid.Add((Bundle(value), true));
        }
        foreach (var item in invalid)
        {
            using var stream = new MemoryStream(item.Bytes);
            AssertTrue(UnityVersionDetector.ReadVersion(stream, item.Bundle) is null,
                "Malformed/unsupported Unity header supplied a version.");
        }

        var root = CreateTestRoot();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            WritePayload(gameRoot, GamePackageLayout.MainLibrary, "main");
            WritePayload(gameRoot, GamePackageLayout.UnityLibrary, "unity");
            WritePayload(gameRoot, GamePackageLayout.Il2CppLibrary, "binary");
            WritePayload(gameRoot, GamePackageLayout.Metadata, "metadata");
            var bundlePath = GamePackageLayout.FilePath(gameRoot, GamePackageLayout.DataBundle);
            File.WriteAllBytes(bundlePath, Bundle("2022.3.62f2"));
            var apk = Path.Combine(root, "game.apk");

            async Task CheckExtraction(string expected)
            {
                if (File.Exists(apk)) File.Delete(apk);
                ZipFile.CreateFromDirectory(gameRoot, apk, CompressionLevel.Optimal, false);
                foreach (var input in new[] { gameRoot, apk })
                {
                    var work = Path.Combine(root, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    var request = new PatchRequest
                    {
                        InputPath = input,
                        OutputPath = input == apk ? Path.Combine(root, "output.apk") : null
                    }.NormalizeAndValidate();
                    AssertEqual(expected, await new GameInteropGenerator(request.Generation!, null).ExtractInputsAsync(work, default));
                    AssertTrue(Directory.GetFiles(work).Select(Path.GetFileName).Order().SequenceEqual(
                        new[] { "global-metadata.dat", "libil2cpp.so" }),
                        "Version detection extracted game assets instead of reading their headers.");
                }
            }

            await CheckExtraction("2022.3.62f2"); // Compressed APK stream with only a bundle.
            var mainData = GamePackageLayout.FilePath(gameRoot, GamePackageLayout.MainData);
            File.WriteAllBytes(mainData, Serialized("2021.3.25f1"));
            await CheckExtraction("2021.3.25f1");
            var managers = GamePackageLayout.FilePath(gameRoot, GamePackageLayout.GlobalGameManagers);
            File.WriteAllBytes(managers, Serialized("6000.3.8f1", 22));
            await CheckExtraction("6000.3.8f1");
            File.WriteAllText(managers, "broken header with decoy 2019.4.0f1");
            File.Delete(mainData);
            await CheckExtraction("2022.3.62f2"); // Malformed standalone file falls back to bundle.
            File.Delete(bundlePath);
            AssertThrows<InvalidDataException>(() => UnityVersionDetector.FromDirectory(gameRoot, null));
            AssertEqual("2020.3.48f1", UnityVersionDetector.FromDirectory(gameRoot, "2020.3.48f1"));
            using var archive = ZipFile.OpenRead(apk);
            AssertEqual("2020.3.48f1", UnityVersionDetector.FromApk(archive, "2020.3.48f1"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            AssertThrows<OperationCanceledException>(() =>
                UnityVersionDetector.FromApk(archive, null, cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Serialized(string version, uint format = 17, byte endian = 0)
    {
        var bytes = new byte[1024];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 128);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 1024);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), format);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 256);
        bytes[16] = endian;
        var offset = 20;
        if (format == 22)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 128);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), 1024);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), 256);
            offset = 48;
        }
        Encoding.ASCII.GetBytes(version + '\0').CopyTo(bytes, offset);
        return bytes;
    }

    private static byte[] Bundle(string version, uint format = 8,
        string signature = "UnityFS", string player = "5.x.x")
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(signature + '\0'));
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, format);
        output.Write(number);
        output.Write(Encoding.ASCII.GetBytes(player + '\0' + version + '\0'));
        output.Write(new byte[1024]); // Represents compressed blocks, never consumed by the detector.
        return output.ToArray();
    }

    private static void AssertHeader(string expected, byte[] bytes, bool bundle, int maximum)
    {
        using var stream = new HeaderOnlyStream(bytes, maximum);
        AssertEqual(expected, UnityVersionDetector.ReadVersion(stream, bundle));
    }

    private sealed class HeaderOnlyStream(byte[] bytes, int maximum) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (inner.Position + buffer.Length > maximum) throw new Exception("Detector read beyond the header.");
            return inner.Read(buffer);
        }
        public override int ReadByte()
        {
            if (inner.Position >= maximum) throw new Exception("Detector read beyond the header.");
            return inner.ReadByte();
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
