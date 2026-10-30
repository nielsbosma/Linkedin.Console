using System.ComponentModel;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Snapshots;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class MeSnapshotCommand : MemberCommand<MeSnapshotCommand.Settings>
{
    public sealed class Settings : MemberSettings
    {
        [CommandOption("--domain <DOMAINS>")]
        [Description("Comma-separated domains. See: linkedin me domains")]
        public string? Domain { get; init; }

        [CommandOption("--limit <N>")]
        [Description("Stop each domain after N rows (stdout only)")]
        public int? Limit { get; init; }

        [CommandOption("--out-dir <DIR>")]
        [Description("Write one JSON file per domain plus manifest.yaml. Required for a full export")]
        public string? OutDir { get; init; }

        [CommandOption("--force")]
        [Description("With --out-dir: fetch finished domains again")]
        public bool Force { get; init; }
    }

    protected override void Check(Settings settings)
    {
        // A whole export must not land in a terminal or an agent's context by accident.
        if (string.IsNullOrWhiteSpace(settings.Domain) && string.IsNullOrWhiteSpace(settings.OutDir))
            throw LinkedInException.Invalid("Pass --domain, or --out-dir for a full export. Your data holds messages, phone numbers and security logs.", "linkedin me domains");
        if (settings.Limit is not null && settings.OutDir is not null)
            throw LinkedInException.Invalid("--limit cannot be used with --out-dir: a shortened file would count as finished.");
        if (settings.Limit is < 1)
            throw LinkedInException.Invalid("--limit must be at least 1.");
        if (settings.Force && settings.OutDir is null)
            throw LinkedInException.Invalid("--force only applies with --out-dir.");
        ParseDomains(settings.Domain);
    }

    protected override async Task<object?> RunAsync(LinkedInApiClient client, Settings settings, CancellationToken ct)
    {
        var domains = ParseDomains(settings.Domain);

        return settings.OutDir is null
            ? await ToStdoutAsync(client, domains!, settings.Limit, ct)
            : await ToFolderAsync(client, domains, settings, ct);
    }

    private static async Task<object> ToStdoutAsync(LinkedInApiClient client, IReadOnlyList<string> domains, int? limit, CancellationToken ct)
    {
        var results = new List<object?>();

        foreach (var domain in domains)
        {
            var rows = new List<object?>();
            var truncated = false;

            await foreach (var element in SnapshotReader.ReadAsync(client, domain, ct))
            {
                rows.AddRange(element.Rows);
                if (rows.Count >= limit)
                {
                    truncated = true;
                    rows = rows.Take(limit.Value).ToList();
                    break;
                }
            }

            results.Add(new Dictionary<string, object?>
            {
                ["domain"] = domain,
                ["status"] = rows.Count == 0 ? ManifestDomain.EmptyOrNotReady : "ok",
                ["rowCount"] = rows.Count,
                ["truncated"] = truncated ? true : null,
                ["rows"] = rows
            });
        }

        return new Dictionary<string, object?> { ["domains"] = results };
    }

    private static async Task<object> ToFolderAsync(LinkedInApiClient client, IReadOnlyList<string>? domains, Settings settings, CancellationToken ct)
    {
        var dir = Path.GetFullPath(settings.OutDir!);
        var exporter = new SnapshotExporter(client, dir, settings.Force, OutputHelper.Status);
        var result = await exporter.ExportAsync(domains, ct);

        return new Dictionary<string, object?>
        {
            ["dir"] = dir,
            ["manifest"] = SnapshotExporter.ManifestFile,
            ["walkComplete"] = domains is null ? result.Manifest.WalkComplete : null,
            ["fetched"] = result.Fetched,
            ["skipped"] = result.Skipped,
            ["emptyOrNotReady"] = domains is null ? null : result.EmptyOrNotReady,
            ["notReturned"] = domains is null ? result.Manifest.NotReturned : null
        };
    }

    private static IReadOnlyList<string>? ParseDomains(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Names become file names as well as query values, so anything else stops here.
        var domains = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DomainCatalog.Spell)
            .Distinct()
            .ToList();

        var invalid = domains.FirstOrDefault(d => !LinkedInApiClient.IsDomainName(d));
        if (invalid is not null)
            throw LinkedInException.Invalid($"'{invalid}' is not a domain name.", "linkedin me domains");

        return domains;
    }
}
