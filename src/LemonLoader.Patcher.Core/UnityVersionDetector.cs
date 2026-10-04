using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace LemonLoader.Patcher.Core;

internal static partial class UnityVersionDetector
{
    private static readonly string[] Candidates =
    [GamePackageLayout.GlobalGameManagers, GamePackageLayout.MainData, GamePackageLayout.DataBundle];

    internal static string FromApk(ZipArchive apk, string? versionOverride,
        CancellationToken cancellationToken = default) =>
        Detect(path => apk.GetEntry(path)?.Open(), versionOverride, cancellationToken);

    internal static string FromDirectory(string directory, string? versionOverride,
        CancellationToken cancellationToken = default) =>
        Detect(path =>
        {
            var file = GamePackageLayout.FilePath(directory, path);
            return File.Exists(file) ? File.OpenRead(file) : null;
        }, versionOverride, cancellationToken);

    private static string Detect(Func<string, Stream?> open, string? versionOverride,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(versionOverride))
            return versionOverride;
        foreach (var path in Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = open(path);
            if (stream is null)
                continue;
            var version = ReadVersion(stream, path == GamePackageLayout.DataBundle);
            if (version is not null)
                return version;
        }
        throw new InvalidDataException(
            "Could not read the Unity version from globalgamemanagers, mainData or data.unity3d. " +
            "Supply --unity-version (Unity version in GUI) for an unsupported or modified game layout.");
    }

    internal static string? ReadVersion(Stream stream, bool bundle)
    {
        try
        {
            var version = bundle ? ReadBundleVersion(stream) : ReadSerializedVersion(stream);
            return version is not null && EngineVersion().IsMatch(version) ? version : null;
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }

    private static string? ReadSerializedVersion(Stream stream)
    {
        Span<byte> header = stackalloc byte[48];
        stream.ReadExactly(header[..20]);
        var format = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (format is < 9 or > 22 || header[16] > 1)
            return null;
        var metadataSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        ulong fileSize = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        ulong dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        uint headerSize = 20;
        if (format == 22)
        {
            // Large-file format replaces the initial sizes with extended fields.
            stream.ReadExactly(header[20..]);
            metadataSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
            fileSize = BinaryPrimitives.ReadUInt64BigEndian(header[24..]);
            dataOffset = BinaryPrimitives.ReadUInt64BigEndian(header[32..]);
            headerSize = 48;
        }
        if (metadataSize == 0 || dataOffset < (ulong)headerSize + metadataSize || dataOffset > fileSize)
            return null;
        return ReadString(stream, (int)Math.Min(metadataSize, 64));
    }

    private static string? ReadBundleVersion(Stream stream)
    {
        var signature = ReadString(stream, 16);
        if (signature is not ("UnityFS" or "UnityWeb" or "UnityRaw"))
            return null;
        Span<byte> formatBytes = stackalloc byte[4];
        stream.ReadExactly(formatBytes);
        var format = BinaryPrimitives.ReadUInt32BigEndian(formatBytes);
        if (format is < 1 or > 8 || (signature == "UnityFS" && format < 6))
            return null;
        _ = ReadString(stream, 64); // Player/compatibility version, often "5.x.x".
        return ReadString(stream, 64); // Actual engine revision; before compressed blocks.
    }

    private static string ReadString(Stream stream, int maximum)
    {
        Span<byte> bytes = stackalloc byte[64];
        for (var i = 0; i < maximum; i++)
        {
            var value = stream.ReadByte();
            if (value < 0)
                throw new EndOfStreamException();
            if (value == 0)
                return Encoding.ASCII.GetString(bytes[..i]);
            if (value is < 32 or > 126)
                throw new InvalidDataException("Invalid Unity header string.");
            bytes[i] = (byte)value;
        }
        throw new InvalidDataException("Unterminated Unity header string.");
    }

    [GeneratedRegex(@"\A[1-9][0-9]*\.[0-9]+\.[0-9]+(?:[abfp][0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex EngineVersion();
}
