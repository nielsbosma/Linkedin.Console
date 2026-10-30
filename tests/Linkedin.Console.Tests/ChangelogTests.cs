using System.Net;
using System.Text.Json;
using Linkedin.Console.Changes;
using Linkedin.Console.Infrastructure;
using Xunit;

namespace Linkedin.Console.Tests;

public class ChangelogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static string Event(string id, long processedAt) =>
        JsonSerializer.Serialize(new { id, processedAt, resourceName = "messages" });

    /// <summary>Behaves as measured: startTime is inclusive, and pages hold at most count events.</summary>
    private static StubHandler Api(params (string Id, long ProcessedAt)[] events) => new(request =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var start = long.TryParse(query["startTime"], out var s) ? s : 0;
        var count = int.Parse(query["count"]!);
        var page = events.Where(e => e.ProcessedAt >= start).OrderBy(e => e.ProcessedAt).Take(count)
            .Select(e => JsonDocument.Parse(Event(e.Id, e.ProcessedAt)).RootElement);
        return (HttpStatusCode.OK, JsonSerializer.Serialize(new { elements = page, paging = new { total = 0 } }));
    });

    private static ChangelogSync Sync(StubHandler stub, TempDir dir, DateTimeOffset? now = null, List<string>? messages = null) =>
        new(stub.Client(), dir.Path, () => now ?? Now, m => messages?.Add(m));

    private static List<string> Lines(TempDir dir) =>
        File.ReadAllLines(Path.Combine(dir.Path, ChangelogSync.EventsFile)).ToList();

    private static string EventsPath(TempDir dir) => Path.Combine(dir.Path, ChangelogSync.EventsFile);

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-1)]
    public void Counts_outside_the_range_are_refused_here(int count) =>
        Assert.Throws<LinkedInException>(() => ChangelogReader.ValidateCount(count));

    [Fact]
    public async Task Reading_pages_on_the_latest_processedAt_and_removes_repeats()
    {
        var stub = Api(("a", 100), ("b", 200), ("c", 300));
        var ids = new List<string>();

        await foreach (var page in ChangelogReader.ReadNewAsync(stub.Client(), null, 2, new HashSet<string>(), _ => { }, CancellationToken.None))
            ids.AddRange(page.Select(e => e.Id));

        Assert.Equal(["a", "b", "c"], ids);
        Assert.Contains("startTime=200", stub.Requests[1]);
    }

    [Fact]
    public async Task A_sync_appends_only_new_events_and_saves_the_position()
    {
        using var dir = new TempDir();

        var first = await Sync(Api(("a", 100), ("b", 200)), dir).RunAsync(10, CancellationToken.None);
        Assert.Equal(2, first.NewEvents);
        Assert.Equal(200, first.Position);

        var stub = Api(("a", 100), ("b", 200), ("c", 300));
        var second = await Sync(stub, dir).RunAsync(10, CancellationToken.None);

        Assert.Equal(1, second.NewEvents);
        Assert.Equal(3, Lines(dir).Count);
        Assert.Contains("startTime=200", stub.Requests[0]);
    }

    [Fact]
    public async Task A_crash_after_writing_but_before_saving_the_position_loses_and_repeats_nothing()
    {
        using var dir = new TempDir();
        await Sync(Api(("a", 100)), dir).RunAsync(10, CancellationToken.None);

        // The crash: an event reached the file, but the saved position still says 100.
        File.AppendAllText(EventsPath(dir), Event("b", 200) + "\n");

        var result = await Sync(Api(("a", 100), ("b", 200), ("c", 300)), dir).RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.NewEvents);
        Assert.Equal(3, Lines(dir).Count);
        Assert.Equal(300, result.Position);
    }

    [Fact]
    public async Task A_lost_state_file_is_rebuilt_from_the_events()
    {
        using var dir = new TempDir();
        await Sync(Api(("a", 100), ("b", 200)), dir).RunAsync(10, CancellationToken.None);
        File.Delete(Path.Combine(dir.Path, ChangelogSync.StateFile));

        var stub = Api(("a", 100), ("b", 200));
        var result = await Sync(stub, dir).RunAsync(10, CancellationToken.None);

        Assert.Equal(0, result.NewEvents);
        Assert.Equal(2, Lines(dir).Count);
        Assert.Contains("startTime=200", stub.Requests[0]);
    }

    [Fact]
    public async Task A_torn_last_line_is_removed_and_its_event_fetched_again()
    {
        using var dir = new TempDir();
        await Sync(Api(("a", 100)), dir).RunAsync(10, CancellationToken.None);
        File.AppendAllText(EventsPath(dir), Event("b", 200)[..10]);

        var result = await Sync(Api(("a", 100), ("b", 200)), dir).RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.NewEvents);
        var lines = Lines(dir);
        Assert.Equal(2, lines.Count);
        foreach (var line in lines) JsonDocument.Parse(line).Dispose();
    }

    [Fact]
    public async Task A_whole_last_line_without_a_newline_is_kept()
    {
        using var dir = new TempDir();
        await Sync(Api(("a", 100)), dir).RunAsync(10, CancellationToken.None);
        File.AppendAllText(EventsPath(dir), Event("b", 200));

        var result = await Sync(Api(("a", 100), ("b", 200), ("c", 300)), dir).RunAsync(10, CancellationToken.None);

        Assert.Equal(1, result.NewEvents);
        Assert.Equal(3, Lines(dir).Count);
    }

    [Fact]
    public async Task More_than_28_days_since_the_last_sync_reports_the_gap_with_dates_and_remembers_it()
    {
        using var dir = new TempDir();
        await Sync(Api(("a", 100)), dir).RunAsync(10, CancellationToken.None);

        var later = Now.AddDays(40);
        var messages = new List<string>();
        var result = await Sync(Api(("a", 100)), dir, later, messages).RunAsync(10, CancellationToken.None);

        Assert.Equal(Now, result.Gap!.From);
        Assert.Equal(later.AddDays(-28), result.Gap.To);
        Assert.Contains(messages, m => m.Contains("2026-10-30"));

        var next = await Sync(Api(("a", 100)), dir, later.AddDays(1)).RunAsync(10, CancellationToken.None);
        Assert.Null(next.Gap);
        Assert.Equal(1, next.KnownGaps);
    }

    [Fact]
    public async Task An_old_position_alone_is_no_gap_because_a_member_can_be_quiet()
    {
        using var dir = new TempDir();
        var oldEvent = Now.AddDays(-100).ToUnixTimeMilliseconds();
        await Sync(Api(("a", oldEvent)), dir).RunAsync(10, CancellationToken.None);

        var result = await Sync(Api(), dir, Now.AddDays(1)).RunAsync(10, CancellationToken.None);

        Assert.Null(result.Gap);
        Assert.Equal(0, result.KnownGaps);
    }

    [Fact]
    public async Task A_failed_run_writes_what_it_had_and_does_not_count_as_a_finished_sync()
    {
        using var dir = new TempDir();
        var calls = 0;
        var stub = new StubHandler(_ => ++calls == 1
            ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { elements = new[] { JsonDocument.Parse(Event("a", 100)).RootElement } }))
            : (HttpStatusCode.TooManyRequests, "{}"));

        await Assert.ThrowsAsync<LinkedInException>(() => Sync(stub, dir).RunAsync(10, CancellationToken.None));

        Assert.Single(Lines(dir));
        var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(Path.Combine(dir.Path, ChangelogSync.StateFile)));
        Assert.Equal(100, state!.Position);
        Assert.Null(state.LastSyncAt);
    }

    [Fact]
    public async Task An_event_without_an_id_stops_without_printing_its_content()
    {
        var stub = new StubHandler(_ => (HttpStatusCode.OK, """{"elements":[{"processedAt":1,"activity":"private words"}]}"""));

        var ex = await Assert.ThrowsAsync<LinkedInException>(async () =>
        {
            await foreach (var _ in ChangelogReader.ReadNewAsync(stub.Client(), null, 10, new HashSet<string>(), _ => { }, CancellationToken.None)) { }
        });

        Assert.DoesNotContain("private words", ex.Message);
    }
}
