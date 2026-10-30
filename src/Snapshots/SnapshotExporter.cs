using System.Text.Json;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Linkedin.Console.Snapshots;

public sealed class ManifestDomain
{
    public const string Complete = "complete";
    public const string EmptyOrNotReady = "empty-or-not-ready";

    public string Status { get; set; } = "";
    public string? File { get; set; }
    public int Rows { get; set; }
    public string? CheckedAt { get; set; }
}

public sealed class SnapshotManifest
{
    public int Version { get; set; } = 1;
    public string ApiVersion { get; set; } = LinkedInApiClient.ApiVersion;
    public bool WalkComplete { get; set; }
    public string? UpdatedAt { get; set; }
    public Dictionary<string, ManifestDomain> Domains { get; set; } = new();
    public List<string> NotReturned { get; set; } = [];
}

public sealed record ExportResult(SnapshotManifest Manifest, List<string> Fetched, List<string> Skipped, List<string> EmptyOrNotReady);

/// <summary>
/// Writes one JSON file per domain plus <c>manifest.yaml</c>. The manifest is saved after every
/// domain, so a stop at LinkedIn's daily limit keeps what is finished and a rerun continues.
/// </summary>
public sealed class SnapshotExporter(LinkedInApiClient client, string dir, bool force, Action<string> status)
{
    public const string ManifestFile = "manifest.yaml";

    private string ManifestPath => Path.Combine(dir, ManifestFile);

    public async Task<ExportResult> ExportAsync(IReadOnlyList<string>? domains, CancellationToken ct)
    {
        ConfigStore.EnsurePrivateDir(dir);
        using var _ = await FileLock.AcquireAsync(Path.Combine(dir, ".lock"), ct);

        var manifest = LoadManifest();
        return domains is null
            ? await ExportEverythingAsync(manifest, ct)
            : await ExportDomainsAsync(manifest, domains, ct);
    }

    private async Task<ExportResult> ExportDomainsAsync(SnapshotManifest manifest, IReadOnlyList<string> domains, CancellationToken ct)
    {
        var result = new ExportResult(manifest, [], [], []);

        foreach (var domain in domains)
        {
            if (!force && manifest.Domains.GetValueOrDefault(domain)?.Status == ManifestDomain.Complete)
            {
                result.Skipped.Add(domain);
                continue;
            }

            var rows = new List<object?>();
            await foreach (var element in SnapshotReader.ReadAsync(client, domain, ct))
                rows.AddRange(element.Rows);

            if (rows.Count == 0)
            {
                status($"{domain}: empty or not ready");
                result.EmptyOrNotReady.Add(domain);
                // An earlier export of this domain stays valid; LinkedIn's 404 can mean "not ready".
                if (manifest.Domains.GetValueOrDefault(domain)?.Status != ManifestDomain.Complete)
                    manifest.Domains[domain] = new ManifestDomain { Status = ManifestDomain.EmptyOrNotReady, CheckedAt = Now() };
            }
            else
            {
                WriteDomain(manifest, domain, rows);
                result.Fetched.Add(domain);
            }
            SaveManifest(manifest);
        }
        return result;
    }

    /// <summary>
    /// Follows LinkedIn's own listing, which found a domain the docs do not have and takes fewer
    /// calls than asking for each name. A rerun walks from the start again rather than from a saved
    /// page: pages may shift when a late domain fills in, and a shift would skip data silently.
    /// Finished domains are not fetched into a file again, only read past.
    /// </summary>
    private async Task<ExportResult> ExportEverythingAsync(SnapshotManifest manifest, CancellationToken ct)
    {
        if (force) manifest = new SnapshotManifest();

        var result = new ExportResult(manifest, [], [], []);
        if (manifest.WalkComplete)
        {
            status("This folder already holds a complete export. Use --force to replace it, or --domain to fetch one domain again.");
            result.Skipped.AddRange(manifest.Domains.Where(d => d.Value.Status == ManifestDomain.Complete).Select(d => d.Key));
            return result;
        }

        var finishedEarlier = manifest.Domains
            .Where(d => d.Value.Status == ManifestDomain.Complete)
            .Select(d => d.Key)
            .ToHashSet();
        var written = new HashSet<string>();

        string? current = null;
        var rows = new List<object?>();

        await foreach (var element in SnapshotReader.ReadAsync(client, null, ct))
        {
            if (element.Domain != current)
            {
                Flush();
                current = element.Domain;
                rows = [];
            }
            if (!finishedEarlier.Contains(element.Domain)) rows.AddRange(element.Rows);
        }
        Flush();

        manifest.WalkComplete = true;
        manifest.NotReturned = DomainCatalog.NotReturned(manifest.Domains.Where(d => d.Value.Status == ManifestDomain.Complete).Select(d => d.Key));
        SaveManifest(manifest);

        result.Skipped.AddRange(finishedEarlier);
        return result;

        void Flush()
        {
            if (current is null || rows.Count == 0) return;

            // The pages measured came grouped by domain, but nothing promises that.
            if (!written.Add(current)) rows.InsertRange(0, ReadDomainRows(current));

            WriteDomain(manifest, current, rows);
            if (!result.Fetched.Contains(current)) result.Fetched.Add(current);
            SaveManifest(manifest);
        }
    }

    private void WriteDomain(SnapshotManifest manifest, string domain, List<object?> rows)
    {
        var file = domain + ".json";
        var payload = new Dictionary<string, object?> { ["domain"] = domain, ["fetchedAt"] = Now(), ["rows"] = rows };

        ConfigStore.AtomicWrite(Path.Combine(dir, file), JsonSerializer.Serialize(payload, OutputHelper.JsonOptions));

        manifest.Domains[domain] = new ManifestDomain { Status = ManifestDomain.Complete, File = file, Rows = rows.Count, CheckedAt = Now() };
        status($"{domain}: {rows.Count} rows");
    }

    private List<object?> ReadDomainRows(string domain)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, domain + ".json")));
        return doc.RootElement.GetProperty("rows").EnumerateArray().Select(YamlOutput.JsonToObject).ToList();
    }

    private SnapshotManifest LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return new SnapshotManifest();

        return new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<SnapshotManifest>(File.ReadAllText(ManifestPath)) ?? new SnapshotManifest();
    }

    private void SaveManifest(SnapshotManifest manifest)
    {
        manifest.UpdatedAt = Now();
        var yaml = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(manifest);
        ConfigStore.AtomicWrite(ManifestPath, yaml);
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}
