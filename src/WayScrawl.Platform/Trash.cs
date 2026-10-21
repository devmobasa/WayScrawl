using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WayScrawl.Platform.Native;

namespace WayScrawl.Platform;

public sealed record TrashItem(string Path, string Original, long FreedBytes, long TrashedBytes, string? Problem = null);

public static class Trash
{
    public static string Destination(string source, string trashRoot, string id) => System.IO.Path.Combine(
        trashRoot, "files", System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(source)) + "-" + id);

    public static TrashItem Move(string source, string trashRoot, DateTimeOffset now, string? itemId = null, string[]? metadataLast = null,
        Action? beforeDelete = null, Action<string>? afterSync = null, CancellationToken cancellationToken = default,
        Action<string>? afterRehashChunk = null, Action<string>? afterUnlink = null, Func<string, int>? syncError = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PathSafety.IsLink(source) || PathSafety.IsLink(trashRoot)) throw new IOException("Trash roots must not be symbolic links.");
        source = PathSafety.CanonicalPath(source);
        trashRoot = PathSafety.CanonicalPath(trashRoot);
        string id = itemId ?? Guid.NewGuid().ToString("N");
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("Trash item id must be a GUID in N format.");
        if (PathSafety.IsLink(trashRoot) || PathSafety.IsLink(source) || source == System.IO.Path.GetFullPath(trashRoot) ||
            PathSafety.Within(source, trashRoot) || PathSafety.Within(trashRoot, source))
            throw new IOException("Unsafe Trash path relation.");
        string files = System.IO.Path.Combine(trashRoot, "files"), info = System.IO.Path.Combine(trashRoot, "info");
        foreach (string directory in new[] { trashRoot, files, info })
        {
            if (PathSafety.IsLink(directory)) throw new IOException("Trash paths must not contain symbolic links.");
            CreateDirectoryDurably(directory, afterSync, syncError);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string destination = Destination(source, trashRoot, id),
               metadata = System.IO.Path.Combine(info, System.IO.Path.GetFileName(destination) + ".trashinfo");
        string contents = "[Trash Info]\nPath=" + Uri.EscapeDataString(source).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase) +
            "\nDeletionDate=" + now.LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "\n";
        if (File.Exists(metadata))
        {
            if (PathSafety.IsLink(metadata) || !File.ReadAllText(metadata).StartsWith(
                                                   contents.Split("\nDeletionDate=", 2)[0] + "\nDeletionDate=", StringComparison.Ordinal))
                throw new IOException("Trash receipt does not match this source.");
        }
        else
        {
            using var file = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false)); writer.Write(contents);
        }
        if (Directory.Exists(destination))
        {
            // A prior cross-filesystem move published its verified copy before deleting the source.
            var verified = Directory.Exists(source) ? Verify(source, destination, requireSameSet: false)
                                                    : new Dictionary<string, VerifiedFile>(StringComparer.Ordinal);
            SyncRetained(destination, metadata, trashRoot, afterSync, syncError);
            beforeDelete?.Invoke();
            var deletion = StorageAccounting.DeleteTree(
                source, metadataLast, verified, cancellationToken, afterRehashChunk, afterUnlink);
            return new(destination, source, deletion.FreedBytes, StorageAccounting.AllocatedBytes(destination), deletion.Problem);
        }
        if (!Directory.Exists(source)) throw new IOException("Trash source and retained item are both missing.");
        if (!PathSafety.InspectTree(source).Safe) throw new IOException("Linked source tree cannot be trashed.");
        long retained = StorageAccounting.AllocatedBytes(source);
        try
        {
            Directory.Move(source, destination);
            return new(destination, source, 0, retained);
        }
        catch (IOException) when (Directory.Exists(source) && !Directory.Exists(destination))
        {
            // Even matching device IDs can cross mount boundaries (EXDEV). A failed rename
            // falls back to verified copy before unlinking; any copy failure keeps the source.
        }
        string temporary = System.IO.Path.Combine(files, "." + System.IO.Path.GetFileName(destination) + ".partial");
        if (Directory.Exists(temporary))
        {
            if (!PathSafety.InspectTree(temporary).Safe) throw new IOException("Linked partial Trash copy.");
            Directory.Delete(temporary, recursive: true); // An unpublished copy; the intact source still exists.
        }
        try
        {
            Copy(source, temporary);
            var verified = Verify(source, temporary, requireSameSet: true);
            Directory.Move(temporary, destination); // Commit the verified copy before deleting any source.
            SyncRetained(destination, metadata, trashRoot, afterSync, syncError);
            beforeDelete?.Invoke();
            var removed = StorageAccounting.DeleteTree(source, metadataLast, verified, cancellationToken, afterRehashChunk, afterUnlink);
            return new(destination, source, removed.FreedBytes, StorageAccounting.AllocatedBytes(destination), removed.Problem);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unpublished copies have no recovery value: the intact source is still here.
            // In particular, release partial allocations immediately after ENOSPC rather
            // than retaining/refilling them on every daily pass.
            if (Directory.Exists(temporary) && PathSafety.InspectTree(temporary).Safe)
            {
                foreach (string child in Directory.GetDirectories(temporary, "*", SearchOption.AllDirectories).Prepend(temporary))
                    File.SetUnixFileMode(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(temporary, recursive: true);
            }
            throw;
        }
    }

    private static void SyncRetained(
        string destination, string metadata, string trashRoot, Action<string>? afterSync, Func<string, int>? syncError)
    {
        if (!PathSafety.InspectTree(destination).Safe) throw new IOException("Linked retained Trash tree.");
        foreach (string file in Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Append(metadata))
        {
            Libc.SyncFile(file, syncError);
            afterSync?.Invoke(file);
        }
        foreach (string directory in Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length).Append(destination).Concat(
                         [System.IO.Path.Combine(trashRoot, "files"), System.IO.Path.Combine(trashRoot, "info"), trashRoot]))
        {
            Libc.SyncDirectory(directory, syncError);
            afterSync?.Invoke(directory);
        }
        // Include existing ancestors too: a previous failed pass may have created their entries without syncing them.
        for (string? parent = System.IO.Path.GetDirectoryName(trashRoot); parent is not null;
             parent = System.IO.Path.GetDirectoryName(parent))
        {
            Libc.SyncDirectory(parent, syncError);
            afterSync?.Invoke(parent);
        }
    }

    private static void CreateDirectoryDurably(string directory, Action<string>? afterSync, Func<string, int>? syncError)
    {
        var missing = new Stack<string>();
        for (string? path = directory; path is not null && !Directory.Exists(path); path = System.IO.Path.GetDirectoryName(path))
            missing.Push(path);
        while (missing.TryPop(out string? path))
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Libc.SyncDirectory(path, syncError);
            afterSync?.Invoke(path);
            string parent = System.IO.Path.GetDirectoryName(path)!;
            Libc.SyncDirectory(parent, syncError);
            afterSync?.Invoke(parent);
        }
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            if (PathSafety.IsLink(entry)) throw new IOException("Source became linked during Trash copy.");
            string target = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(entry));
            if (Directory.Exists(entry)) Copy(entry, target);
            else
            {
                File.Copy(entry, target);
                File.SetUnixFileMode(target, File.GetUnixFileMode(entry));
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(entry));
            }
        }
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
    }

    private static Dictionary<string, VerifiedFile> Verify(string source, string copy, bool requireSameSet)
    {
        if (!PathSafety.InspectTree(source).Safe || !PathSafety.InspectTree(copy).Safe)
            throw new IOException("Linked Trash transfer tree.");
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var names = files.Select(f => System.IO.Path.GetRelativePath(source, f)).ToHashSet(StringComparer.Ordinal);
        if (requireSameSet &&
            !names.SetEquals(
                Directory.GetFiles(copy, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(copy, f))))
            throw new IOException("Source changed during Trash copy.");
        var verified = new Dictionary<string, VerifiedFile>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            var stat = FileAllocation.Read(file);
            var identity = FileAllocation.Identity(file);
            string retained = System.IO.Path.Combine(copy, System.IO.Path.GetRelativePath(source, file));
            using var original = File.OpenRead(file); using var backup = File.OpenRead(retained);
            byte[] hash = SHA256.HashData(original);
            if (!hash.AsSpan().SequenceEqual(SHA256.HashData(backup)) || identity != FileAllocation.Identity(file))
                throw new IOException("Source changed during Trash copy: " + file);
            verified.Add(System.IO.Path.GetFullPath(file),
                new(identity, stat.Size, stat.ModifiedSeconds, stat.ModifiedNanoseconds, hash));
        }
        return verified;
    }
}
