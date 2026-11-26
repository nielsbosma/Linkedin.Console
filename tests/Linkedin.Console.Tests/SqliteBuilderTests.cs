using System.Text.Json;
using Linkedin.Console.Changes;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Snapshots;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Linkedin.Console.Tests;

public class SqliteBuilderTests
{
    private static void Export(TempDir dir, bool complete = true, params (string Domain, string RowsJson)[] domains)
    {
        var manifest = $"version: 1\napiVersion: 202312\nwalkComplete: {complete.ToString().ToLowerInvariant()}\ndomains:\n";
        foreach (var (domain, rows) in domains)
        {
            manifest += $"  {domain}:\n    status: complete\n    file: {domain}.json\n";
            File.WriteAllText(Path.Combine(dir.Path, domain + ".json"),
                $$"""{"domain":"{{domain}}","fetchedAt":"2026-10-30T14:00:00Z","rows":{{rows}}}""");
        }
        File.WriteAllText(Path.Combine(dir.Path, SnapshotExporter.ManifestFile), manifest);
    }

    private static SqliteBuildResult Build(TempDir from, string output, TempDir? changes = null, bool force = false, List<string>? messages = null) =>
        SqliteBuilder.Build(from.Path, changes?.Path, output, force, m => messages?.Add(m));

    private static object? Scalar(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Each_domain_becomes_a_table_with_rows_of_different_shapes()
    {
        using var dir = new TempDir();
        Export(dir, domains: [("CONNECTIONS", """[{"First Name":"Åsa","Company":"A"},{"First Name":"Bo","URL":"u"}]"""), ("Events", """[{"Name":"e"}]""")]);
        var db = Path.Combine(dir.Path, "out.db");

        var result = Build(dir, db);

        Assert.Equal(2, result.Tables);
        Assert.Equal(3, result.Rows);
        Assert.Equal("Åsa", Scalar(db, """SELECT "First Name" FROM CONNECTIONS WHERE _row = 1"""));
        Assert.Equal("u", Scalar(db, "SELECT URL FROM CONNECTIONS WHERE _row = 2"));
        Assert.Equal(DBNull.Value, Scalar(db, "SELECT Company FROM CONNECTIONS WHERE _row = 2"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM Events"));
        Assert.Equal(2L, Scalar(db, "SELECT rows FROM _domains WHERE domain = 'CONNECTIONS'"));
        Assert.Equal("true", Scalar(db, "SELECT value FROM _meta WHERE key = 'export_complete'"));
    }

    [Fact]
    public void Odd_keys_survive_as_columns()
    {
        using var dir = new TempDir();
        Export(dir, domains: [("ARTICLES", """[{"<html>":"a","Name":"b","name":"c","_row":"d","say \"hi\"":"e"}]""")]);
        var db = Path.Combine(dir.Path, "out.db");

        Build(dir, db);

        Assert.Equal("a", Scalar(db, """SELECT "<html>" FROM ARTICLES"""));
        Assert.Equal("b", Scalar(db, "SELECT Name FROM ARTICLES"));
        Assert.Equal("c", Scalar(db, "SELECT name_2 FROM ARTICLES"));
        Assert.Equal("d", Scalar(db, "SELECT _row_2 FROM ARTICLES"));
        Assert.Equal("e", Scalar(db, """SELECT "say ""hi""" FROM ARTICLES"""));
    }

    [Fact]
    public void A_domain_named_like_a_reserved_table_does_not_collide()
    {
        using var dir = new TempDir();
        Export(dir, domains: [("CHANGELOG", """[{"A":"x"}]""")]);
        var db = Path.Combine(dir.Path, "out.db");

        Build(dir, db);

        Assert.Equal("CHANGELOG_2", Scalar(db, "SELECT table_name FROM _domains"));
        Assert.Equal("x", Scalar(db, "SELECT A FROM CHANGELOG_2"));
    }

    [Fact]
    public void Only_finished_domains_in_the_manifest_go_in()
    {
        using var dir = new TempDir();
        Export(dir, domains: [("SKILLS", """[{"Name":"x"}]""")]);
        File.WriteAllText(Path.Combine(dir.Path, "STRAY.json"), """{"domain":"STRAY","rows":[{"A":"b"}]}""");
        File.AppendAllText(Path.Combine(dir.Path, SnapshotExporter.ManifestFile), "  CONNECTIONS:\n    status: empty-or-not-ready\n");
        var db = Path.Combine(dir.Path, "out.db");

        var result = Build(dir, db);

        Assert.Equal(1, result.Tables);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('STRAY', 'CONNECTIONS')") is 0L ? 1L : 0L);
    }

    [Fact]
    public void An_unfinished_export_is_reported_and_recorded()
    {
        using var dir = new TempDir();
        Export(dir, complete: false, domains: [("SKILLS", """[{"Name":"x"}]""")]);
        var db = Path.Combine(dir.Path, "out.db");

        Assert.False(Build(dir, db).ExportComplete);
        Assert.Equal("false", Scalar(db, "SELECT value FROM _meta WHERE key = 'export_complete'"));
    }

    [Fact]
    public void Events_go_into_the_changelog_and_a_bad_line_is_left_out_with_a_warning()
    {
        using var dir = new TempDir();
        using var changes = new TempDir();
        Export(dir, domains: [("SKILLS", """[{"Name":"x"}]""")]);
        File.WriteAllLines(Path.Combine(changes.Path, ChangelogSync.EventsFile),
        [
            JsonSerializer.Serialize(new { id = 1, processedAt = 100, capturedAt = 90, resourceName = "messages", method = "CREATE", activityStatus = "SUCCESS" }),
            "not json",
            JsonSerializer.Serialize(new { id = 1, processedAt = 100 }),
            JsonSerializer.Serialize(new { id = 2, processedAt = 200 })
        ]);
        var db = Path.Combine(dir.Path, "out.db");
        var messages = new List<string>();

        var result = Build(dir, db, changes, messages: messages);

        Assert.Equal(2, result.Events);
        Assert.Equal("messages", Scalar(db, "SELECT resource_name FROM changelog WHERE id = '1'"));
        Assert.Equal(90L, Scalar(db, "SELECT captured_at FROM changelog WHERE id = '1'"));
        Assert.Contains(messages, m => m.Contains("line 2"));
    }

    [Fact]
    public void An_existing_file_is_kept_unless_forced()
    {
        using var dir = new TempDir();
        Export(dir, domains: [("SKILLS", """[{"Name":"x"}]""")]);
        var db = Path.Combine(dir.Path, "out.db");
        File.WriteAllText(db, "keep me");

        Assert.Throws<LinkedInException>(() => Build(dir, db));
        Assert.Equal("keep me", File.ReadAllText(db));

        Build(dir, db, force: true);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM SKILLS"));
    }

    [Fact]
    public void A_failed_build_leaves_the_old_database_and_no_temp_file()
    {
        using var dir = new TempDir();
        Export(dir, domains: [("SKILLS", """[{"Name":"x"}]""")]);
        var db = Path.Combine(dir.Path, "out.db");
        Build(dir, db);

        File.Delete(Path.Combine(dir.Path, "SKILLS.json"));

        Assert.Throws<LinkedInException>(() => Build(dir, db, force: true));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM SKILLS"));
        Assert.False(File.Exists(db + ".tmp"));
    }

    [Fact]
    public void A_folder_without_an_export_says_what_to_run()
    {
        using var dir = new TempDir();

        var ex = Assert.Throws<LinkedInException>(() => Build(dir, Path.Combine(dir.Path, "out.db")));

        Assert.Equal(ErrorCode.InvalidInput, ex.Code);
        Assert.Contains("linkedin me snapshot", ex.Remediation);
    }
}
