namespace WayScrawl.Checks;

internal sealed class CheckFailed(string message) : Exception(message);

internal sealed class Harness
{
    private readonly List<string> temporary = [];
    public int Count { get; private set; }
    public string Section { get; set; } = "";

    public void Check(bool value, string message)
    {
        if (!value) throw new CheckFailed($"[{Section}] {message}");
        Count++;
    }

    public void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException
                                       or ArgumentException) { rejected = true; }
        Check(rejected, message);
    }

    public async Task RejectAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        bool rejected = false;
        try { await action(); }
        catch (TException) { rejected = true; }
        Check(rejected, message);
    }

    public string TempDirectory(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wayscrawl-checks-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        temporary.Add(path);
        return path;
    }

    public void Cleanup()
    {
        foreach (string path in temporary)
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
    }
}
