using System.Net;
using System.Text.Json;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Snapshots;
using Xunit;

namespace Linkedin.Console.Tests;

public class SnapshotTests
{
    private const string NoData = """{"message":"No data found for this domain and memberId.","status":404}""";

    private static string Page(string domain, params string[] values)
    {
        var rows = values.Select(v => new Dictionary<string, string> { ["Value"] = v });
        return JsonSerializer.Serialize(new { elements = new[] { new { snapshotDomain = domain, snapshotData = rows } } });
    }

    /// <summary>The listing with no domain: each page belongs to one domain, then a 404.</summary>
    private static StubHandler Listing(params (string Domain, string Value)[] pages) => new(request =>
    {
        var start = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["start"]!);
        return start < pages.Length
            ? (HttpStatusCode.OK, Page(pages[start].Domain, pages[start].Value))
            : (HttpStatusCode.NotFound, NoData);
    });

    private static SnapshotExporter Exporter(StubHandler stub, TempDir dir, bool force = false) =>
        new(stub.Client(), dir.Path, force, _ => { });

    [Fact]
    public async Task Reading_follows_pages_until_the_404_and_sends_the_version()
    {
        var stub = Listing(("ARTICLES", "a"), ("ARTICLES", "b"));
        var rows = new List<object?>();

        await foreach (var element in SnapshotReader.ReadAsync(stub.Client(), "ARTICLES", CancellationToken.None))
            rows.AddRange(element.Rows);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Contains("domain=ARTICLES&start=2", stub.Requests[2]);
    }

    [Fact]
    public async Task A_429_is_retried_and_then_succeeds()
    {
        var calls = 0;
        var stub = new StubHandler(_ => ++calls == 1
            ? (HttpStatusCode.TooManyRequests, "{}")
            : (HttpStatusCode.OK, Page("SKILLS", "x")));

        var page = await stub.Client().GetSnapshotPageAsync("SKILLS", 0, CancellationToken.None);

        Assert.Equal("SKILLS", page![0].Domain);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_lasting_429_stops_with_rate_limited()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.TooManyRequests, "{}"));

        var ex = await Assert.ThrowsAsync<LinkedInException>(
            () => stub.Client().GetSnapshotPageAsync("SKILLS", 0, CancellationToken.None));

        Assert.Equal(ErrorCode.RateLimited, ex.Code);
        Assert.Equal(4, stub.Requests.Count);
    }

    [Fact]
    public async Task An_unsupported_domain_is_named_in_the_error()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.BadRequest, "{}"));

        var ex = await Assert.ThrowsAsync<LinkedInException>(
            () => stub.Client().GetSnapshotPageAsync("LOGIN", 0, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidInput, ex.Code);
        Assert.Contains("LOGIN", ex.Message);
    }

    [Fact]
    public async Task An_expired_token_says_how_to_fix_it_and_hides_urns()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.Unauthorized, """{"message":"bad token for urn:li:person:abc"}"""));

        var ex = await Assert.ThrowsAsync<LinkedInException>(
            () => stub.Client().IsChangeLoggingOnAsync(CancellationToken.None));

        Assert.Equal(ErrorCode.AuthRequired, ex.Code);
        Assert.Contains("linkedin auth token", ex.Remediation);
        Assert.DoesNotContain("urn:li:person", ex.Detail);
    }

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("../evil")]
    [InlineData("a b")]
    public void Names_that_could_leave_the_folder_do_not_count_as_domains(string name) =>
        Assert.False(LinkedInApiClient.IsDomainName(name));

    [Fact]
    public void A_mixed_case_name_from_LinkedIn_counts_as_a_domain() =>
        Assert.True(LinkedInApiClient.IsDomainName("Events"));

    [Theory]
    [InlineData("skills", "SKILLS")]
    [InlineData("EVENTS", "Events")]
    [InlineData("new_one", "NEW_ONE")]
    public void Input_is_spelled_the_way_LinkedIn_wants(string input, string expected) =>
        Assert.Equal(expected, DomainCatalog.Spell(input));

    [Fact]
    public async Task A_path_in_a_returned_domain_name_never_reaches_the_disk()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, Page("../evil", "x")));
        using var dir = new TempDir();

        await Assert.ThrowsAsync<LinkedInException>(() => Exporter(stub, dir).ExportAsync(null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_full_export_groups_pages_by_domain_and_a_rerun_makes_no_calls()
    {
        using var dir = new TempDir();
        var stub = Listing(("PROFILE", "p"), ("ARTICLES", "a1"), ("ARTICLES", "a2"));

        var first = await Exporter(stub, dir).ExportAsync(null, CancellationToken.None);

        Assert.True(first.Manifest.WalkComplete);
        Assert.Equal(["PROFILE", "ARTICLES"], first.Fetched);
        Assert.Equal(2, first.Manifest.Domains["ARTICLES"].Rows);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, "ARTICLES.json"))))
            Assert.Equal(2, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Contains("CONNECTIONS", first.Manifest.NotReturned);

        var before = stub.Requests.Count;
        var second = await Exporter(stub, dir).ExportAsync(null, CancellationToken.None);

        Assert.Equal(before, stub.Requests.Count);
        Assert.Empty(second.Fetched);
    }

    [Fact]
    public async Task A_stop_at_the_limit_keeps_finished_domains_and_a_rerun_finishes_the_rest()
    {
        using var dir = new TempDir();
        var limited = true;
        var stub = new StubHandler(request =>
        {
            var start = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["start"]!);
            if (limited && start == 2) return (HttpStatusCode.TooManyRequests, "{}");
            return start switch
            {
                0 => (HttpStatusCode.OK, Page("PROFILE", "p")),
                1 => (HttpStatusCode.OK, Page("ARTICLES", "a1")),
                2 => (HttpStatusCode.OK, Page("ARTICLES", "a2")),
                _ => (HttpStatusCode.NotFound, NoData)
            };
        });

        await Assert.ThrowsAsync<LinkedInException>(() => Exporter(stub, dir).ExportAsync(null, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(dir.Path, "PROFILE.json")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "ARTICLES.json")));

        limited = false;
        var written = File.GetLastWriteTimeUtc(Path.Combine(dir.Path, "PROFILE.json"));
        var result = await Exporter(stub, dir).ExportAsync(null, CancellationToken.None);

        Assert.True(result.Manifest.WalkComplete);
        Assert.Equal(["ARTICLES"], result.Fetched);
        Assert.Equal(["PROFILE"], result.Skipped);
        Assert.Equal(2, result.Manifest.Domains["ARTICLES"].Rows);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(dir.Path, "PROFILE.json")));
    }

    [Fact]
    public async Task Pages_of_one_domain_that_are_not_together_are_merged()
    {
        using var dir = new TempDir();
        var stub = Listing(("SKILLS", "a"), ("PROFILE", "p"), ("SKILLS", "b"));

        var result = await Exporter(stub, dir).ExportAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Manifest.Domains["SKILLS"].Rows);
    }

    [Fact]
    public async Task An_empty_domain_is_not_marked_finished_so_a_rerun_asks_again()
    {
        using var dir = new TempDir();
        var hasData = false;
        var stub = new StubHandler(request =>
        {
            var start = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["start"]!);
            return hasData && start == 0 ? (HttpStatusCode.OK, Page("CONNECTIONS", "c")) : (HttpStatusCode.NotFound, NoData);
        });

        var first = await Exporter(stub, dir).ExportAsync(["CONNECTIONS"], CancellationToken.None);

        Assert.Equal(["CONNECTIONS"], first.EmptyOrNotReady);
        Assert.Equal(ManifestDomain.EmptyOrNotReady, first.Manifest.Domains["CONNECTIONS"].Status);
        Assert.False(File.Exists(Path.Combine(dir.Path, "CONNECTIONS.json")));

        hasData = true;
        var second = await Exporter(stub, dir).ExportAsync(["CONNECTIONS"], CancellationToken.None);

        Assert.Equal(["CONNECTIONS"], second.Fetched);
        Assert.Equal(ManifestDomain.Complete, second.Manifest.Domains["CONNECTIONS"].Status);
    }

    [Fact]
    public async Task A_finished_domain_is_skipped_unless_forced()
    {
        using var dir = new TempDir();
        var stub = Listing(("SKILLS", "a"));

        await Exporter(stub, dir).ExportAsync(["SKILLS"], CancellationToken.None);
        var calls = stub.Requests.Count;

        var skipped = await Exporter(stub, dir).ExportAsync(["SKILLS"], CancellationToken.None);
        Assert.Equal(["SKILLS"], skipped.Skipped);
        Assert.Equal(calls, stub.Requests.Count);

        var forced = await Exporter(stub, dir, force: true).ExportAsync(["SKILLS"], CancellationToken.None);
        Assert.Equal(["SKILLS"], forced.Fetched);
        Assert.True(stub.Requests.Count > calls);
    }
}
