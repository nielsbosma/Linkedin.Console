using System.Text;
using System.Text.Json;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;

namespace Linkedin.Console.Changes;

public sealed class SyncGap
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
}

public sealed class SyncState
{
    /// <summary>The latest <c>processedAt</c> in the file, in epoch milliseconds.</summary>
    public long? Position { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Kept here as well as printed: a scheduled run's output is usually gone by the time it matters.</summary>
    public List<SyncGap> Gaps { get; set; } = [];
}

public sealed record SyncResult(int NewEvents, int TotalEvents, long? Position, SyncGap? Gap, int KnownGaps);

/// <summary>
/// Appends new events to <c>changelog.ndjson</c>, so history can outlast LinkedIn's 28 days.
///
/// The position is saved only after the file is flushed, so a crash repeats events rather than
/// losing them; the repeats are then removed by reading the ids already in the file.
/// </summary>
public sealed class ChangelogSync(LinkedInApiClient client, string dir, Func<DateTimeOffset> now, Action<string> status)
{
    public const string EventsFile = "changelog.ndjson";
    public const string StateFile = "sync-state.json";

    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = OutputHelper.JsonOptions.Encoder };

    private string EventsPath => Path.Combine(dir, EventsFile);
    private string StatePath => Path.Combine(dir, StateFile);

    public async Task<SyncResult> RunAsync(int count, CancellationToken ct)
    {
        ConfigStore.EnsurePrivateDir(dir);
        using var _ = await FileLock.AcquireAsync(Path.Combine(dir, ".lock"), ct);

        var state = LoadState();
        RepairTail();

        var (seen, latest) = ScanEvents();
        // The file wins over the state if the state was lost or the file got ahead of it in a crash.
        if (latest > (state.Position ?? 0)) state.Position = latest;

        var gap = FindGap(state);

        var added = 0;
        await foreach (var page in ChangelogReader.ReadNewAsync(client, state.Position, count, seen, status, ct))
        {
            Append(page);
            added += page.Count;
            state.Position = Math.Max(state.Position ?? 0, page.Max(e => e.ProcessedAt));
            SaveState(state);
        }

        // The gap is measured from the last finished run: an old position only means a quiet member.
        state.LastSyncAt = now();
        SaveState(state);

        return new SyncResult(added, seen.Count, state.Position, gap, state.Gaps.Count);
    }

    private SyncGap? FindGap(SyncState state)
    {
        if (state.LastSyncAt is not { } last) return null;

        var windowStart = now() - ChangelogReader.Retention;
        if (last >= windowStart) return null;

        var gap = new SyncGap { From = last, To = windowStart };
        state.Gaps.Add(gap);
        SaveState(state);
        status($"warning: the last sync was on {last:yyyy-MM-dd HH:mm} UTC and LinkedIn keeps 28 days. Events from {gap.From:yyyy-MM-dd HH:mm} to {gap.To:yyyy-MM-dd HH:mm} UTC are lost.");
        return gap;
    }

    private void Append(IReadOnlyList<ChangeEvent> events)
    {
        var isNew = !File.Exists(EventsPath);
        using (var stream = new FileStream(EventsPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var bytes = new UTF8Encoding(false).GetBytes(
                string.Concat(events.Select(e => JsonSerializer.Serialize(e.Raw, LineOptions) + "\n")));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (isNew) ConfigStore.RestrictToOwner(EventsPath);
    }

    /// <summary>
    /// A crash mid-write leaves a partial last line, which the next append would glue onto a good
    /// event. Its event is past the saved position, so dropping the fragment loses nothing.
    /// </summary>
    private void RepairTail()
    {
        if (!File.Exists(EventsPath)) return;

        var bytes = File.ReadAllBytes(EventsPath);
        if (bytes.Length == 0 || bytes[^1] == (byte)'\n') return;

        var lineStart = Array.LastIndexOf(bytes, (byte)'\n') + 1;

        using var stream = new FileStream(EventsPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        if (TryParse(bytes.AsSpan(lineStart)) is not null)
        {
            stream.Seek(0, SeekOrigin.End);
            stream.WriteByte((byte)'\n');
        }
        else
        {
            stream.SetLength(lineStart);
            status("Removed a partial last line from an interrupted sync.");
        }
        stream.Flush(flushToDisk: true);
    }

    private (HashSet<string> Seen, long? Latest) ScanEvents()
    {
        var seen = new HashSet<string>();
        long? latest = null;
        if (!File.Exists(EventsPath)) return (seen, latest);

        var lineNumber = 0;
        foreach (var line in File.ReadLines(EventsPath))
        {
            lineNumber++;
            if (line.Length == 0) continue;

            var parsed = TryParse(Encoding.UTF8.GetBytes(line));
            if (parsed is null)
            {
                status($"warning: line {lineNumber} of {EventsFile} is not a valid event; left as it is.");
                continue;
            }
            seen.Add(parsed.Id);
            if (parsed.ProcessedAt > (latest ?? 0)) latest = parsed.ProcessedAt;
        }
        return (seen, latest);
    }

    private static ChangeEvent? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            return ChangeEvent.Parse(doc.RootElement.Clone());
        }
        catch (Exception ex) when (ex is JsonException or LinkedInException)
        {
            return null;
        }
    }

    private SyncState LoadState() =>
        File.Exists(StatePath)
            ? JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath)) ?? new SyncState()
            : new SyncState();

    private void SaveState(SyncState state) =>
        ConfigStore.AtomicWrite(StatePath, JsonSerializer.Serialize(state, OutputHelper.JsonOptions));
}
