using System.ComponentModel;
using System.Globalization;
using Linkedin.Console.Changes;
using Linkedin.Console.Infrastructure;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class MeChangelogCommand : MemberCommand<MeChangelogCommand.Settings>
{
    public sealed class Settings : MemberSettings
    {
        [CommandOption("--since <DATE>")]
        [Description("Only events from this date or time, in UTC unless it says otherwise. LinkedIn keeps 28 days")]
        public string? Since { get; init; }

        [CommandOption("--limit <N>")]
        [Description("Stop after N events, oldest first (default: 100)")]
        public int Limit { get; init; } = 100;

        [CommandOption("--count <N>")]
        [Description("Events per API call, from 1 to 50 (default: 10, as LinkedIn recommends)")]
        public int Count { get; init; } = ChangelogReader.DefaultCount;
    }

    protected override void Check(Settings settings)
    {
        if (settings.Limit < 1) throw LinkedInException.Invalid("--limit must be at least 1.");
        ChangelogReader.ValidateCount(settings.Count);
        ParseSince(settings.Since);
    }

    protected override async Task<object?> RunAsync(LinkedInApiClient client, Settings settings, CancellationToken ct)
    {
        var since = ParseSince(settings.Since);

        if (since is { } from && from < DateTimeOffset.UtcNow - ChangelogReader.Retention)
            OutputHelper.Status("warning: LinkedIn only keeps the last 28 days, so nothing older will come back.");

        var events = new List<ChangeEvent>();
        var truncated = false;

        await foreach (var page in ChangelogReader.ReadNewAsync(
            client, since?.ToUnixTimeMilliseconds(), settings.Count, new HashSet<string>(), OutputHelper.Status, ct))
        {
            events.AddRange(page);
            if (events.Count >= settings.Limit)
            {
                truncated = true;
                events = events.Take(settings.Limit).ToList();
                break;
            }
        }

        return new Dictionary<string, object?>
        {
            ["count"] = events.Count,
            ["truncated"] = truncated ? true : null,
            ["nextSince"] = truncated
                ? DateTimeOffset.FromUnixTimeMilliseconds(events[^1].ProcessedAt).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                : null,
            ["events"] = events.Select(e => YamlOutput.JsonToObject(e.Raw)).ToList()
        };
    }

    private static DateTimeOffset? ParseSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since)
            ? since
            : throw LinkedInException.Invalid($"'{value}' is not a date. Use a form like 2026-10-30 or 2026-10-30T14:00:00Z.");
    }
}
