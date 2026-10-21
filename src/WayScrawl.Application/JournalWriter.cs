using System.Text;
using System.Text.Json;
using WayScrawl.Core;

namespace WayScrawl.Application;

// Appends journal.jsonl lines as they happen, so a crash keeps everything before it.
public sealed class JournalWriter : IDisposable
{
    private readonly FileStream stream;
    private readonly Lock gate = new();
    private long sequence;

    public JournalWriter(string path)
    {
        stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public long NextSequence() => Interlocked.Increment(ref sequence);

    public void Event(TimelineEvent item) => Write(new(JournalEntry.EventType, item.AtUs, Event: item));
    public void Pin(Pin pin) => Write(new(JournalEntry.PinType, pin.AtUs, Pin: pin));
    public void Stroke(Stroke stroke) => Write(new(JournalEntry.StrokeType, stroke.Points[^1].AtUs, Stroke: stroke));
    public void Removed(long atUs, string[] ids, string reason) =>
        Write(new(JournalEntry.RemovedType, atUs, StrokeIds: ids, Reason: reason));
    public void Source(InteractionSourceInfo info) => Write(new(JournalEntry.SourceType, 0, Source: info));

    private void Write(JournalEntry entry)
    {
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, WayScrawlJson.Journal) + "\n");
        lock (gate)
        {
            stream.Write(line);
            stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (gate) stream.Dispose();
    }
}
