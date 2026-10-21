using System.Buffers.Binary;
using System.ComponentModel;
using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Core;

namespace WayScrawl.Review;

// Validated PNG bytes, read once and decoded from memory.
public sealed record PngAsset(string Path, byte[] Bytes, int Length, int Width, int Height)
{
    public MemoryStream Open() => new(Bytes, 0, Length, writable: false);
}

// The inspector never follows image paths from a manifest; the review UI must. It opens only the
// asset names the bundle writer produces, only inside the bundle and never through a symlink, and
// caps the file size and the declared pixel count before any decoder sees the bytes. A racing
// local writer can still swap files after the checks: bundles are trusted local output, not imports.
public static class BundleAssets
{
    public const long MaxImageBytes = 64L * 1024 * 1024;
    // 8K frames and contact sheets fit easily; a small PNG declaring a huge canvas does not.
    public const long MaxPixels = 64L * 1024 * 1024;
    private const int MaxEdge = 32 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsImagePath(string? path) =>
        path == Bundle.ContactSheet || path is not null && BundleRules.IsAssetPath(path, "frames", ".png");

    public static PngAsset ReadPng(string bundleDirectory, string relativePath)
    {
        if (!IsImagePath(relativePath))
            throw new InvalidDataException($"{relativePath} is not a bundle image path.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleDirectory));
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        string folder = Path.GetDirectoryName(full) ?? root;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"{relativePath} resolves outside the bundle.");
        if (folder != root && new DirectoryInfo(folder).LinkTarget is not null)
            throw new InvalidDataException($"{Path.GetFileName(folder)}/ is a symbolic link.");
        var file = new FileInfo(full);
        if (!file.Exists) throw new FileNotFoundException($"{relativePath} is missing.", full);
        if (file.LinkTarget is not null) throw new InvalidDataException($"{relativePath} is a symbolic link.");
        // Also rejects FIFOs and devices, which report no length and could block the open below.
        if (file.Length < 33) throw new InvalidDataException($"{relativePath} is too small to be a PNG.");
        if (file.Length > MaxImageBytes) throw new InvalidDataException($"{relativePath} exceeds 64 MiB; skipped.");

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length > MaxImageBytes) throw new InvalidDataException($"{relativePath} exceeds 64 MiB; skipped.");
        // One spare byte detects a file that grew after the length check without reading past the cap.
        byte[] bytes = new byte[length + 1];
        int total = 0, read;
        while (total < bytes.Length && (read = stream.Read(bytes, total, bytes.Length - total)) > 0) total += read;
        if (total > length) throw new InvalidDataException($"{relativePath} changed while it was read.");

        var header = bytes.AsSpan(0, total);
        if (total < 33 || !header[..8].SequenceEqual(Signature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException($"{relativePath} is not a PNG.");
        int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        if (width is <= 0 or > MaxEdge || height is <= 0 or > MaxEdge || (long)width * height > MaxPixels)
            throw new InvalidDataException($"{relativePath} declares {width}x{height} pixels; skipped.");
        return new(relativePath, bytes, total, width, height);
    }
}

internal static class Errors
{
    // What a bundle on disk can make the reader throw; anything else is a bug and should surface.
    public static bool IsBundle(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or ArgumentException or NotSupportedException;

    // Bundle errors plus what desktop helpers and the daemon socket can throw
    // (SocketException is a Win32Exception).
    public static bool IsAction(Exception error) => IsBundle(error) || error is Win32Exception
        or InvalidOperationException or DaemonUnavailableException;
}
