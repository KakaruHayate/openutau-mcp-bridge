using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Records document changes so an agent can poll instead of re-reading everything.
/// Entries are derived from <see cref="UCommand.Impact"/>, which is the host's own
/// description of a command's blast radius.
/// </summary>
internal sealed class ChangeFeed : ICmdSubscriber
{
    public sealed record Entry(
        long Sequence,
        string Command,
        string Kind,
        string? PartId,
        int? TrackNo,
        IReadOnlyList<string>? Curves,
        string At);

    private const int Capacity = 512;

    private readonly object gate = new();
    private readonly Queue<Entry> buffer = new();
    private long sequence;
    private bool attached;

    public static ChangeFeed Instance { get; } = new();

    public long Sequence
    {
        get { lock (gate) { return sequence; } }
    }

    /// <summary>Attaches on first use; the host's command stream is process-wide.</summary>
    public void EnsureAttached()
    {
        if (attached)
        {
            return;
        }
        try
        {
            DocManager.Inst.AddSubscriber(this);
            attached = true;
        }
        catch (Exception e)
        {
            StatusFile.AppendError("change-feed", e);
        }
    }

    public void OnNext(UCommand cmd, bool isUndo)
    {
        if (cmd is UNotification)
        {
            return;
        }

        var kind = "Unknown";
        string? partId = null;
        int? trackNo = null;
        IReadOnlyList<string>? curves = null;
        try
        {
            var impact = cmd.Impact;
            kind = impact.Kind.ToString();
            partId = impact.Part?.Id.ToString();
            trackNo = impact.Track?.TrackNo;
            curves = impact.CurveAbbrs;
        }
        catch
        {
            // A command is free to override Impact with anything; a broken one must not
            // take down the feed.
        }

        lock (gate)
        {
            sequence++;
            buffer.Enqueue(new Entry(sequence, cmd.GetType().Name, kind, partId, trackNo, curves,
                DateTimeOffset.UtcNow.ToString("O")));
            while (buffer.Count > Capacity)
            {
                buffer.Dequeue();
            }
        }
    }

    /// <summary>
    /// Entries after <paramref name="sinceSequence"/>. When that point has already been
    /// evicted, the caller must take a fresh snapshot instead of trusting a gap.
    /// </summary>
    public IReadOnlyList<Entry> Since(long sinceSequence, int limit, out bool requiresSnapshot)
    {
        lock (gate)
        {
            // The oldest sequence still answerable. An empty buffer means "nothing since
            // the last entry", so the next answerable point is sequence + 1.
            var oldest = buffer.Count > 0 ? buffer.Peek().Sequence : sequence + 1;
            requiresSnapshot = sinceSequence > sequence || sinceSequence < oldest - 1;
            if (requiresSnapshot)
            {
                return Array.Empty<Entry>();
            }
            return buffer.Where(e => e.Sequence > sinceSequence).Take(limit).ToList();
        }
    }

    /// <summary>
    /// The document was replaced: drop the buffered entries so callers holding an old
    /// sequence are told to take a fresh snapshot.
    /// </summary>
    public void MarkSessionBoundary()
    {
        lock (gate)
        {
            buffer.Clear();
        }
    }
}

