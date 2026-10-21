namespace WayScrawl.Platform;

public static class PathSafety
{
    public static bool IsLink(string path)
    {
        // Managed entries may not themselves be links. XDG and /home ancestors can
        // legitimately be aliases; InspectTree checks every entry below this root.
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static string CanonicalPath(string path)
    {
        string full = Path.GetFullPath(path);
        var tail = new Stack<string>();
        while (!File.Exists(full) && !Directory.Exists(full) && !IsLink(full))
        {
            tail.Push(Path.GetFileName(full));
            full = Path.GetDirectoryName(full) ?? throw new IOException("No existing path ancestor.");
        }
        full = Native.Libc.RealPath(full);
        while (tail.TryPop(out string? component)) full = Path.Combine(full, component);
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool Within(string root, string path)
    {
        string full = CanonicalPath(path), parent = CanonicalPath(root);
        return full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static (bool Safe, DateTime LastChanged, long LogicalBytes) InspectTree(string root)
    {
        if (IsLink(root)) return (false, DateTime.MaxValue, 0);
        DateTime changed = File.GetLastWriteTimeUtc(root);
        long bytes = 0;
        if (File.Exists(root)) return (true, changed, new FileInfo(root).Length);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return (false, DateTime.MaxValue, 0);
            var nested = InspectTree(entry);
            if (!nested.Safe) return nested;
            bytes += nested.LogicalBytes;
            // Zoom is unmanaged scratch, so it does not refresh the retention age.
            if (Path.GetFileName(entry) != "zoom") changed = changed > nested.LastChanged ? changed : nested.LastChanged;
        }
        return (true, changed, bytes);
    }
}
