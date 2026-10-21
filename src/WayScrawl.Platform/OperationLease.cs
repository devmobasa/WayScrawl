using System.Security.Cryptography;
using System.Text;

namespace WayScrawl.Platform;

// Cooperating capture/processing/media/retention operations lock the same directory identity.
public static class OperationLease
{
    public static bool Available(string directory, string runtime)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.CanonicalPath(directory))));
        string file = Path.Combine(runtime, "locks", key + ".lock");
        if (PathSafety.IsLink(directory) || PathSafety.IsLink(Path.Combine(runtime, "locks")) || PathSafety.IsLink(file)) return false;
        if (!File.Exists(file)) return true;
        try { using var lease = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    public static FileStream Acquire(string directory, string runtime)
    {
        if (PathSafety.IsLink(directory)) throw new IOException("Operation directories must not themselves be symlinks.");
        string root = Path.Combine(runtime, "locks");
        if (PathSafety.IsLink(root)) throw new IOException("Operation lock directory must not contain symlinks.");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.CanonicalPath(directory))));
        string file = Path.Combine(root, key + ".lock");
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Operation lock must not be a symlink.");
        return new(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
