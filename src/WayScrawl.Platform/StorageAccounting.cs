using System.Security.Cryptography;
using WayScrawl.Platform.Native;

namespace WayScrawl.Platform;

public sealed record VerifiedFile(FileIdentity Identity, ulong Size, long ModifiedSeconds, uint ModifiedNanoseconds, byte[] Sha256)
{
    internal (FileIdentity Identity, uint Links)? Revalidate(
        string path, CancellationToken cancellationToken, Action<string>? afterRehashChunk)
    {
        var stat = FileAllocation.Read(path);
        var version = (new FileIdentity(stat.DeviceMajor, stat.DeviceMinor, stat.Inode,
            stat.ChangedSeconds, stat.ChangedNanoseconds), stat.Links);
        if (!MatchesVersion(path, version)) return null;
        using var file = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = file.Read(buffer);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            afterRehashChunk?.Invoke(path);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hash.GetHashAndReset().AsSpan().SequenceEqual(Sha256) && MatchesVersion(path, version) ? version : null;
    }

    internal bool MatchesVersion(string path, (FileIdentity Identity, uint Links) expected)
    {
        var stat = FileAllocation.Read(path);
        var identity = new FileIdentity(stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.ChangedSeconds, stat.ChangedNanoseconds);
        return Identity.SameInode(identity) && expected.Identity == identity && expected.Links == stat.Links &&
            Size == stat.Size && ModifiedSeconds == stat.ModifiedSeconds && ModifiedNanoseconds == stat.ModifiedNanoseconds;
    }
}

public sealed record StorageRemoval(long FreedBytes, string? Problem);

public static class StorageAccounting
{
    private sealed record Entry(string Path, uint Major, uint Minor, ulong Inode, uint Links, long Allocated);

    private static Entry[] Capture(string root)
    {
        if (!File.Exists(root) && !Directory.Exists(root)) return [];
        if (!PathSafety.InspectTree(root).Safe) throw new IOException("Linked paths cannot be retired.");
        var files = Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [root];
        return files.Select(path =>
        {
            var stat = FileAllocation.Read(path);
            if ((stat.Mode & 0xf000) != 0x8000) throw new IOException("Only regular payload files can be retired: " + path);
            return new Entry(path, stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.Links, checked((long)stat.Blocks * 512));
        }).ToArray();
    }

    public static long AllocatedBytes(string root) => AllocatedBytes([root]);

    public static long AllocatedBytes(IEnumerable<string> roots) => roots.SelectMany(Capture).DistinctBy(e => e.Path)
        .GroupBy(e => (e.Major, e.Minor, e.Inode)).Sum(g => g.First().Allocated);

    public static long EstimateFreed(IEnumerable<string> roots) => roots.SelectMany(Capture).DistinctBy(e => e.Path)
        .GroupBy(e => (e.Major, e.Minor, e.Inode)).Where(g => g.Count() >= g.First().Links).Sum(g => g.First().Allocated);

    public static StorageRemoval DeleteTree(
        string root, string[]? metadataLast = null, IReadOnlyDictionary<string, VerifiedFile>? verifiedFiles = null,
        CancellationToken cancellationToken = default, Action<string>? afterRehashChunk = null, Action<string>? afterUnlink = null)
    {
        var before = Capture(root);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var metadata = (metadataLast ?? []).Select(name => Path.GetFullPath(Path.Combine(root, name))).ToHashSet(StringComparer.Ordinal);
        string? problem = null;
        var ready = new HashSet<string>(StringComparer.Ordinal);
        var identities = new Dictionary<(uint Major, uint Minor, ulong Inode), (FileIdentity Identity, uint Links)>();
        try
        {
            // Preflight every file before any unlink; changed versions require another cancellable hash.
            foreach (var entry in before)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (verifiedFiles is null) continue;
                string path = Path.GetFullPath(entry.Path);
                if (!verifiedFiles.TryGetValue(path, out var verified) ||
                    verified.Revalidate(path, cancellationToken, afterRehashChunk) is not { } identity)
                {
                    problem ??= "Source changed after Trash verification; transfer pending: " + path;
                    continue;
                }
                ready.Add(path);
                identities[(entry.Major, entry.Minor, entry.Inode)] = identity;
            }
            cancellationToken.ThrowIfCancellationRequested();
            void Unlink(Entry entry)
            {
                string path = Path.GetFullPath(entry.Path);
                var key = (entry.Major, entry.Minor, entry.Inode);
                if (verifiedFiles is not null)
                {
                    if (!ready.Contains(path)) return;
                    var verified = verifiedFiles[path];
                    if (!verified.MatchesVersion(path, identities[key]))
                    {
                        // Even our own unlink changes ctime/nlink: accept a new version only with matching bytes.
                        if (verified.Revalidate(path, cancellationToken, afterRehashChunk) is not { } refreshed)
                        {
                            problem ??= "Source changed after Trash verification; transfer pending: " + path;
                            return;
                        }
                        identities[key] = refreshed;
                    }
                    if (!verified.MatchesVersion(path, identities[key]))
                    {
                        problem ??= "Source changed after Trash verification; transfer pending: " + path;
                        return;
                    }
                }
                File.Delete(path);
                removed.Add(path);
                afterUnlink?.Invoke(path);
            }
            foreach (var entry in before.Where(e => !metadata.Contains(Path.GetFullPath(e.Path)))) Unlink(entry);
            if (problem is not null) return Result();
            if (Directory.Exists(root))
            {
                foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                             .OrderByDescending(p => p.Length))
                    Directory.Delete(directory);
                // Keep discoverable identity/recovery state through directory-removal failures.
                foreach (var entry in before.Where(e => metadata.Contains(Path.GetFullPath(e.Path)))) Unlink(entry);
                if (problem is null) Directory.Delete(root);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { problem = "Source rehash cancelled; transfer pending."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = error.Message; }
        return Result();

        StorageRemoval Result() => new(before.GroupBy(e => (e.Major, e.Minor, e.Inode))
            .Where(g => g.Count(e => removed.Contains(Path.GetFullPath(e.Path))) >= g.First().Links)
            .Sum(g => g.First().Allocated), problem);
    }
}
