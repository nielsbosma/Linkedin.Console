using System.ComponentModel;
using Linkedin.Console.Auth;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Snapshots;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class MeDomainsCommand : LinkedInCommand<MeDomainsCommand.Settings>
{
    public sealed class Settings : MemberSettings
    {
        [CommandOption("--available")]
        [Description("Ask LinkedIn which domains hold data right now (reads every page of your data, about 30 calls)")]
        public bool Available { get; init; }
    }

    protected override async Task<object?> RunAsync(Settings settings, CancellationToken ct)
    {
        if (!settings.Available)
        {
            return new Dictionary<string, object?>
            {
                ["domains"] = DomainCatalog.All.Select(d => new Dictionary<string, object?>
                {
                    ["domain"] = d.Name,
                    ["description"] = d.Description,
                    ["supported"] = d.Supported ? null : false,
                    ["documented"] = d.Documented ? null : false
                }).ToList<object?>()
            };
        }

        var token = TokenResolver.Resolve(settings.AccessToken);
        using var client = new LinkedInApiClient(token.Token);

        // Rows are counted and dropped: this command answers "what is there", not "what does it say".
        var rows = new Dictionary<string, int>();
        await foreach (var element in SnapshotReader.ReadAsync(client, null, ct))
            rows[element.Domain] = rows.GetValueOrDefault(element.Domain) + element.Rows.Count;

        return new Dictionary<string, object?>
        {
            ["domains"] = rows.Select(kv => new Dictionary<string, object?>
            {
                ["domain"] = kv.Key,
                ["rows"] = kv.Value,
                ["description"] = DomainCatalog.Find(kv.Key)?.Description
            }).ToList<object?>(),
            ["notReturned"] = DomainCatalog.NotReturned(rows.Keys)
        };
    }
}
