using System.Text.Json;
using Linkedin.Console.Changes;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;
using Microsoft.Data.Sqlite;

namespace Linkedin.Console.Snapshots;

public sealed record SqliteBuildResult(int Tables, int Rows, int Events, bool ExportComplete);

/// <summary>
/// Builds a SQLite database from an export folder, with no API calls. One table per domain, and
/// every value stays the text LinkedIn sent, because each domain formats its dates its own way.
/// The manifest decides what goes in, so a stray file in the folder does not.
/// </summary>
public static class SqliteBuilder
{
    public const string DomainsTable = "_domains";
    public const string MetaTable = "_meta";
    public const string ChangelogTable = "changelog";

    public static SqliteBuildResult Build(string exportDir, string? changesDir, string output, bool force, Action<string> status)
    {
        var manifestPath = Path.Combine(exportDir, SnapshotExporter.ManifestFile);
        if (!File.Exists(manifestPath))
            throw LinkedInException.Invalid($"No {SnapshotExporter.ManifestFile} in {exportDir}.", "linkedin me snapshot --out-dir <DIR>");

        var manifest = SnapshotExporter.ReadManifest(manifestPath);
        var domains = manifest.Domains
            .Where(d => d.Value.Status == ManifestDomain.Complete)
            .Select(d => d.Key)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        if (domains.Count == 0)
            throw LinkedInException.Invalid($"The export in {exportDir} has no finished domains.", "linkedin me snapshot --out-dir <DIR>");

        var eventsPath = changesDir is null ? null : Path.Combine(changesDir, ChangelogSync.EventsFile);
        if (eventsPath is not null && !File.Exists(eventsPath))
            throw LinkedInException.Invalid($"No {ChangelogSync.EventsFile} in {changesDir}.", "linkedin me sync --dir <DIR>");

        if (File.Exists(output) && !force)
            throw LinkedInException.Invalid($"{output} already exists.", "Pass --force to replace it, or choose another --out.");

        var parent = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(parent)) ConfigStore.EnsurePrivateDir(parent);

        // Built beside the target and moved at the end, so a failure never leaves half a database
        // that looks like a snapshot, and --force keeps the old one until the new one is whole.
        var temp = output + ".tmp";
        File.Delete(temp);
        try
        {
            var result = Write(temp, exportDir, manifest, domains, eventsPath, status);
            ConfigStore.RestrictToOwner(temp);
            File.Move(temp, output, overwrite: true);
            return result;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static SqliteBuildResult Write(
        string path, string exportDir, SnapshotManifest manifest, List<string> domains, string? eventsPath, Action<string> status)
    {
        // Without Pooling=False the closed connection keeps the file open, and the move fails on Windows.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction();

        Execute(db, $"CREATE TABLE {DomainsTable} (domain TEXT, table_name TEXT, rows INTEGER, columns INTEGER, fetched_at TEXT)");
        Execute(db, $"CREATE TABLE {MetaTable} (key TEXT PRIMARY KEY, value TEXT)");
        Execute(db, $"CREATE TABLE {ChangelogTable} (id TEXT PRIMARY KEY, processed_at INTEGER, captured_at INTEGER, resource_name TEXT, method TEXT, activity_status TEXT, raw TEXT)");

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DomainsTable, MetaTable, ChangelogTable };
        var totalRows = 0;

        foreach (var domain in domains)
        {
            var rows = AddDomain(db, exportDir, domain, tables);
            totalRows += rows;
            status($"{domain}: {rows} rows");
        }

        var events = eventsPath is null ? 0 : AddEvents(db, eventsPath, status);

        var meta = new Dictionary<string, string>
        {
            ["built_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["api_version"] = manifest.ApiVersion,
            ["export_updated_at"] = manifest.UpdatedAt ?? "",
            ["export_complete"] = manifest.WalkComplete ? "true" : "false",
            ["not_returned"] = string.Join(",", manifest.NotReturned)
        };
        foreach (var (key, value) in meta)
            Execute(db, $"INSERT INTO {MetaTable} VALUES ($k, $v)", ("$k", key), ("$v", value));

        transaction.Commit();
        return new SqliteBuildResult(domains.Count, totalRows, events, manifest.WalkComplete);
    }

    private static int AddDomain(SqliteConnection db, string exportDir, string domain, HashSet<string> tables)
    {
        if (!LinkedInApiClient.IsDomainName(domain))
            throw new LinkedInException(ErrorCode.Error, $"The manifest names '{domain}', which is not a domain.");

        var file = Path.Combine(exportDir, domain + ".json");
        if (!File.Exists(file))
            throw new LinkedInException(ErrorCode.Error, $"The manifest lists {domain}, but {file} is missing.", remediation: "Run the export again with --force.");

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var fetchedAt = doc.RootElement.TryGetProperty("fetchedAt", out var at) ? at.GetString() : null;
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().Select(RowFields).ToList();

        // SQLite names ignore case and LinkedIn's keys don't, so two keys can need two columns.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_row" };
        var columns = new Dictionary<string, string>();
        foreach (var key in rows.SelectMany(r => r.Keys))
            if (!columns.ContainsKey(key))
                columns[key] = Unique(key, taken);

        var table = Unique(domain, tables);
        var definition = string.Concat(columns.Values.Select(c => $", {Quote(c)} TEXT"));
        Execute(db, $"CREATE TABLE {Quote(table)} (\"_row\" INTEGER PRIMARY KEY{definition})");

        if (columns.Count > 0)
        {
            var keys = columns.Keys.ToList();
            using var insert = db.CreateCommand();
            insert.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(", ", keys.Select(k => Quote(columns[k])))}) " +
                                 $"VALUES ({string.Join(", ", keys.Select((_, i) => "$p" + i))})";
            var parameters = keys.Select((_, i) => insert.Parameters.Add("$p" + i, SqliteType.Text)).ToList();

            foreach (var row in rows)
            {
                for (var i = 0; i < keys.Count; i++)
                    parameters[i].Value = row.TryGetValue(keys[i], out var value) && value is not null ? value : DBNull.Value;
                insert.ExecuteNonQuery();
            }
        }

        Execute(db, $"INSERT INTO {DomainsTable} VALUES ($d, $t, $r, $c, $f)",
            ("$d", domain), ("$t", table), ("$r", rows.Count), ("$c", columns.Count), ("$f", (object?)fetchedAt ?? DBNull.Value));
        return rows.Count;
    }

    private static Dictionary<string, string?> RowFields(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string?> { ["value"] = Text(row) };

        return row.EnumerateObject().GroupBy(p => p.Name).ToDictionary(g => g.Key, g => Text(g.Last().Value));
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText()
    };

    private static int AddEvents(SqliteConnection db, string path, Action<string> status)
    {
        var added = 0;
        var lineNumber = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ChangeEvent parsed;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
                parsed = ChangeEvent.Parse(doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or LinkedInException)
            {
                status($"warning: line {lineNumber} of {ChangelogSync.EventsFile} is not a valid event; left out.");
                continue;
            }

            using (doc)
            {
                var root = parsed.Raw;
                added += Execute(db, $"INSERT OR IGNORE INTO {ChangelogTable} VALUES ($i, $p, $c, $r, $m, $a, $j)",
                    ("$i", parsed.Id), ("$p", parsed.ProcessedAt),
                    ("$c", root.TryGetProperty("capturedAt", out var c) && c.TryGetInt64(out var captured) ? captured : DBNull.Value),
                    ("$r", Field(root, "resourceName")), ("$m", Field(root, "method")), ("$a", Field(root, "activityStatus")),
                    ("$j", line));
            }
        }
        return added;
    }

    private static object Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && Text(value) is { } text ? text : DBNull.Value;

    private static int Execute(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++) candidate = $"{name}_{n}";
        return candidate;
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
