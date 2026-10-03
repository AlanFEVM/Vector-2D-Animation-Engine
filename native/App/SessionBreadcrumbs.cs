using System.Text;

namespace VectorAnimationEngine;

/// <summary>
/// Keeps a short ring of user-facing actions and editor state so a crash report can show what the
/// user was doing. This is intentionally cheap: entries are plain strings and the ring is bounded,
/// because the tracker runs on the UI thread during normal interaction.
/// </summary>
internal static class SessionBreadcrumbs
{
    private const int MaxBreadcrumbs = 120;
    private static readonly object Sync = new();
    private static readonly Queue<Breadcrumb> Entries = new();
    private static long _sequence;

    private readonly record struct Breadcrumb(long Sequence, DateTime TimestampUtc, string Category, string Detail);

    /// <summary>
    /// Records a user-visible action such as a tool switch, selection change, or file operation.
    /// </summary>
    internal static void Record(string category, string detail)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        lock (Sync)
        {
            Entries.Enqueue(new Breadcrumb(++_sequence, DateTime.UtcNow, category, detail ?? ""));
            while (Entries.Count > MaxBreadcrumbs) Entries.Dequeue();
        }
    }

    /// <summary>
    /// Records a state snapshot describing the editor at the moment of the call. Used at natural
    /// checkpoints so a later crash has a recent, meaningful picture of the session.
    /// </summary>
    internal static void RecordState(string detail) => Record("State", detail);

    internal static string Format()
    {
        lock (Sync)
        {
            if (Entries.Count == 0) return "(no breadcrumbs recorded)";
            var builder = new StringBuilder();
            foreach (var entry in Entries)
            {
                builder.Append(entry.TimestampUtc.ToString("HH:mm:ss.fff"))
                    .Append(" [").Append(entry.Category).Append("] ")
                    .Append(entry.Detail)
                    .AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
    }

    internal static int Count
    {
        get
        {
            lock (Sync)
            {
                return Entries.Count;
            }
        }
    }

    internal static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
            _sequence = 0;
        }
    }
}
