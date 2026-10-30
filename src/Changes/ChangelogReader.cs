using System.Runtime.CompilerServices;
using System.Text.Json;
using Linkedin.Console.Infrastructure;

namespace Linkedin.Console.Changes;

public sealed record ChangeEvent(string Id, long ProcessedAt, JsonElement Raw)
{
    public static ChangeEvent Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var id)
            || !element.TryGetProperty("processedAt", out var processedAt)
            || !processedAt.TryGetInt64(out var at))
            throw new LinkedInException(ErrorCode.Error, "LinkedIn returned a change event without an id or processedAt.");

        return new ChangeEvent(id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText(), at, element);
    }
}

public static class ChangelogReader
{
    public const int MinCount = 1;
    public const int MaxCount = 50;

    /// <summary>LinkedIn's recommendation.</summary>
    public const int DefaultCount = 10;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(28);

    /// <summary>LinkedIn answers 200 to 0 and 51, against its docs, so the range is checked here.</summary>
    public static void ValidateCount(int count)
    {
        if (count is < MinCount or > MaxCount)
            throw LinkedInException.Invalid($"--count must be between {MinCount} and {MaxCount}.");
    }

    /// <summary>
    /// Pages of events not in <paramref name="seen"/>, oldest first. <c>startTime</c> is inclusive
    /// and the next one is the latest <c>processedAt</c>, so the last event of a page comes back on
    /// the next; that is what <paramref name="seen"/> removes, and a page with nothing new ends the
    /// read. LinkedIn's <c>total</c> stayed 0 with an event returned, so it is not used.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<ChangeEvent>> ReadNewAsync(
        LinkedInApiClient client, long? startTime, int count, ISet<string> seen, Action<string> status,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var position = startTime;

        while (true)
        {
            var page = (await client.GetChangeLogsAsync(position, count, ct)).Select(ChangeEvent.Parse).ToList();
            var fresh = page.Where(e => seen.Add(e.Id)).OrderBy(e => e.ProcessedAt).ToList();

            if (fresh.Count == 0)
            {
                // A full page of known events means more than a page share one timestamp, and
                // startTime has no way past them.
                if (page.Count >= count)
                    status($"warning: {page.Count} events share one timestamp, so later events may be out of reach. Use a higher --count.");
                yield break;
            }

            yield return fresh;
            position = page.Max(e => e.ProcessedAt);
        }
    }
}
