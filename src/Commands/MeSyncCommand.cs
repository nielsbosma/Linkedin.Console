using System.ComponentModel;
using System.Globalization;
using Linkedin.Console.Changes;
using Linkedin.Console.Infrastructure;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class MeSyncCommand : MemberCommand<MeSyncCommand.Settings>
{
    public sealed class Settings : MemberSettings
    {
        [CommandOption("--dir <DIR>")]
        [Description("Folder that holds changelog.ndjson and the saved position")]
        public required string Dir { get; init; }

        [CommandOption("--count <N>")]
        [Description("Events per API call, from 1 to 50 (default: 10, as LinkedIn recommends)")]
        public int Count { get; init; } = ChangelogReader.DefaultCount;
    }

    protected override void Check(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Dir)) throw LinkedInException.Invalid("--dir is required.");
        ChangelogReader.ValidateCount(settings.Count);
    }

    protected override async Task<object?> RunAsync(LinkedInApiClient client, Settings settings, CancellationToken ct)
    {
        var dir = Path.GetFullPath(settings.Dir);
        var sync = new ChangelogSync(client, dir, () => DateTimeOffset.UtcNow, OutputHelper.Status);
        var result = await sync.RunAsync(settings.Count, ct);

        return new Dictionary<string, object?>
        {
            ["dir"] = dir,
            ["file"] = ChangelogSync.EventsFile,
            ["newEvents"] = result.NewEvents,
            ["totalEvents"] = result.TotalEvents,
            ["position"] = result.Position is { } at
                ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                : null,
            ["gap"] = result.Gap is { } gap
                ? new Dictionary<string, object?> { ["from"] = gap.From.ToString("u"), ["to"] = gap.To.ToString("u") }
                : null,
            ["knownGaps"] = result.KnownGaps > 0 ? result.KnownGaps : null
        };
    }
}
