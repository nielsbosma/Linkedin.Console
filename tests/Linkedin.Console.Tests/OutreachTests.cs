using Linkedin.Console.Infrastructure;
using Linkedin.Console.Outreach;
using Xunit;

namespace Linkedin.Console.Tests;

public class OutreachTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private const string Link = "https://www.linkedin.com/in/";

    private sealed class Desk(params string?[] answers)
    {
        private readonly Queue<string?> _answers = new(answers);

        public List<string> Output { get; } = [];
        public List<string> Copied { get; } = [];
        public List<string> Opened { get; } = [];
        public bool CopyWorks { get; init; } = true;
        public bool OpenWorks { get; init; } = true;
        public string Text => string.Join("\n", Output);

        public OutreachSession Session(OutreachLedger ledger, DateTimeOffset? now = null) => new(
            ledger,
            () => now ?? Now,
            () => _answers.Count > 0 ? _answers.Dequeue() : null,
            Output.Add,
            text => { Copied.Add(text); return CopyWorks; },
            url => { Opened.Add(url); return OpenWorks; });
    }

    private static OutreachPerson Person(string key, PersonState state = PersonState.Waiting, string? note = "Hello", DateTimeOffset? sentAt = null) =>
        new() { Key = key, Url = Link + key, Note = note, State = state, AddedAt = Now, MarkedSentAt = sentAt };

    private static async Task<OutreachLedger> Ledger(TempDir dir, params OutreachPerson[] people)
    {
        var ledger = new OutreachLedger(dir.Path);
        await ledger.UpdateAsync(data => { data.People.AddRange(people); return true; }, CancellationToken.None);
        return ledger;
    }

    private static Task<OutreachData> Read(OutreachLedger ledger) => ledger.ReadAsync(CancellationToken.None);

    private static OutreachPerson[] Sent(int count, DateTimeOffset at) =>
        Enumerable.Range(0, count).Select(i => Person($"sent-{i}", PersonState.MarkedSent, sentAt: at)).ToArray();

    // The CSV

    [Fact]
    public void A_note_can_hold_commas_quotes_and_line_breaks()
    {
        var people = OutreachCsv.Parse($"url,firstName,note\r\n{Link}anna,Anna,\"Hi, \"\"Anna\"\"\r\nnice talk\"\r\n");

        var anna = Assert.Single(people);
        Assert.Equal("Anna", anna.FirstName);
        Assert.Equal("Hi, \"Anna\"\r\nnice talk", anna.Note);
    }

    [Fact]
    public void Excel_semicolons_are_understood_and_a_comma_stays_in_the_note()
    {
        var anna = Assert.Single(OutreachCsv.Parse($"url;note\n{Link}anna;Hi, Anna\n"));
        Assert.Equal("Hi, Anna", anna.Note);
    }

    [Fact]
    public void Columns_can_come_in_any_order_and_case_and_only_url_is_needed()
    {
        var people = OutreachCsv.Parse($"NOTE,URL\nHi,{Link}anna\n,{Link}bo\n");

        Assert.Equal("Hi", people[0].Note);
        Assert.Null(people[1].Note);
    }

    [Fact]
    public void A_note_of_exactly_the_limit_is_fine()
    {
        var note = new string('a', OutreachCsv.NoteLimit);
        Assert.Equal(note, Assert.Single(OutreachCsv.Parse($"url,note\n{Link}anna,{note}\n")).Note);
    }

    [Fact]
    public void A_bad_file_is_refused_whole_and_names_every_line()
    {
        var csv = $"url,note\n{Link}anna,fine\nnot-a-link,hi\n{Link}bo,{new string('a', OutreachCsv.NoteLimit + 1)}\n{Link}cy,Hi, there\n";

        var ex = Assert.Throws<LinkedInException>(() => OutreachCsv.Parse(csv));

        Assert.Equal(ErrorCode.InvalidInput, ex.Code);
        Assert.Contains("3 problems", ex.Message);
        Assert.Contains("line 3", ex.Detail);
        Assert.Contains("line 4", ex.Detail);
        Assert.Contains("line 5", ex.Detail);
    }

    [Fact]
    public void Line_numbers_stay_right_after_a_note_with_line_breaks()
    {
        var ex = Assert.Throws<LinkedInException>(() =>
            OutreachCsv.Parse($"url,note\n{Link}anna,\"one\ntwo\"\nnot-a-link,hi\n"));

        Assert.Contains("line 4", ex.Detail);
    }

    [Theory]
    [InlineData("note\nhi\n")]
    [InlineData("")]
    [InlineData("url,note\nhttps://www.linkedin.com/in/anna,\"never closed\n")]
    [InlineData("url,note\nhttps://www.linkedin.com/in/anna,Malm�\n")]
    public void Files_that_cannot_be_read_are_refused(string csv) =>
        Assert.Throws<LinkedInException>(() => OutreachCsv.Parse(csv));

    // Links

    [Theory]
    [InlineData("https://www.linkedin.com/in/Anna-B/", "https://www.linkedin.com/in/Anna-B", "anna-b")]
    [InlineData("linkedin.com/in/anna-b?utm_source=x", "https://www.linkedin.com/in/anna-b", "anna-b")]
    [InlineData("https://se.linkedin.com/in/anna-b#about", "https://www.linkedin.com/in/anna-b", "anna-b")]
    [InlineData("https://www.linkedin.com/in/%C3%A5sa", "https://www.linkedin.com/in/%C3%A5sa", "åsa")]
    [InlineData("https://www.linkedin.com/in/ACoAABc", "https://www.linkedin.com/in/ACoAABc", "acoaabc")]
    public void Links_are_rebuilt_and_compared_by_handle(string input, string url, string key) =>
        Assert.Equal((url, key), LinkedInUrl.CanonicalProfile(input));

    [Fact]
    public void A_link_can_only_ever_open_on_linkedin()
    {
        var (url, _) = LinkedInUrl.CanonicalProfile("https://www.linkedin.com/in/a%26calc.exe%20%2F..%2F..%2Fevil");

        Assert.StartsWith(Link, url);
        Assert.DoesNotContain("&", url);
        Assert.DoesNotContain(" ", url);
        Assert.Equal(url[Link.Length..], Uri.EscapeDataString(Uri.UnescapeDataString(url[Link.Length..])));
    }

    [Theory]
    [InlineData("https://www.linkedin.com/company/filestar")]
    [InlineData("https://example.com/in/anna")]
    [InlineData("")]
    public void Anything_else_is_not_a_profile(string input) =>
        Assert.Throws<ArgumentException>(() => LinkedInUrl.CanonicalProfile(input));

    // Adding

    [Fact]
    public void Adding_never_touches_someone_already_listed()
    {
        var data = new OutreachData { People = [Person("anna", PersonState.Never, "Old")] };

        var result = OutreachCsv.AddTo(data, OutreachCsv.Parse($"url,note\n{Link}ANNA/,New\n{Link}bo,Hi\n{Link}bo,Again\n"), Now);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.AlreadyListed);
        Assert.Equal(PersonState.Never, data.Find("anna")!.State);
        Assert.Equal("Old", data.Find("anna")!.Note);
        Assert.Equal("Hi", data.Find("bo")!.Note);
    }

    [Fact]
    public void Adding_counts_what_deserves_a_warning()
    {
        var csv = $"url,note\n{Link}a,{new string('a', OutreachCsv.FreeNoteLimit + 1)}\n{Link}b,\n{Link}ACoAABc,Hi\n";

        var result = OutreachCsv.AddTo(new OutreachData(), OutreachCsv.Parse(csv), Now);

        Assert.Equal(1, result.OverReportedFreeLimit);
        Assert.Equal(1, result.WithoutNote);
        Assert.Equal(1, result.Obfuscated);
    }

    // The ledger

    [Fact]
    public async Task The_ledger_survives_a_reload_with_its_names_readable()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("åsa", PersonState.MarkedSent, "Hej då", Now));

        var person = (await Read(new OutreachLedger(dir.Path))).Find("åsa")!;

        Assert.Equal(PersonState.MarkedSent, person.State);
        Assert.Equal(Now, person.MarkedSentAt);
        Assert.Contains("Hej då", File.ReadAllText(Path.Combine(dir.Path, OutreachLedger.FileName)));
    }

    [Fact]
    public async Task A_damaged_ledger_stops_everything_rather_than_starting_empty()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, OutreachLedger.FileName);
        await File.WriteAllTextAsync(path, "{ not json");

        var ex = await Assert.ThrowsAsync<LinkedInException>(() =>
            new OutreachLedger(dir.Path).UpdateAsync(_ => true, CancellationToken.None));

        Assert.Contains("could not be read", ex.Message);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Reading_a_folder_that_does_not_exist_creates_nothing()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "missing");

        Assert.Empty((await Read(new OutreachLedger(missing))).People);
        Assert.False(Directory.Exists(missing));
    }

    // Caps

    [Fact]
    public void Caps_count_a_day_and_the_last_seven_days()
    {
        var data = new OutreachData();
        data.People.AddRange(Sent(2, Now));
        data.People.Add(Person("yesterday", PersonState.MarkedSent, sentAt: Now.AddDays(-1)));
        data.People.Add(Person("edge", PersonState.MarkedSent, sentAt: Now.AddDays(-6.9)));
        data.People.Add(Person("old", PersonState.MarkedSent, sentAt: Now.AddDays(-8)));
        data.People.Add(Person("skipped", PersonState.Skipped));

        var caps = OutreachCaps.Count(data, Now);

        Assert.Equal(2, caps.Today);
        Assert.Equal(4, caps.LastSevenDays);
    }

    [Fact]
    public void Someone_marked_never_after_the_send_still_counts()
    {
        var data = new OutreachData { People = [Person("anna", PersonState.Never, sentAt: Now)] };
        Assert.Equal(1, OutreachCaps.Count(data, Now).Today);
    }

    [Fact]
    public void A_full_week_says_when_the_next_slot_opens()
    {
        var data = new OutreachData();
        data.People.AddRange(Sent(OutreachCaps.Weekly, Now.AddDays(-2)));
        data.People.Add(Person("oldest", PersonState.MarkedSent, sentAt: Now.AddDays(-3)));

        var caps = OutreachCaps.Count(data, Now);

        Assert.True(caps.WeekFull);
        Assert.Equal(Now.AddDays(-2).AddDays(7), caps.NextWeeklySlot);
    }

    // A session

    [Fact]
    public async Task Sent_copies_opens_and_records_then_asks_about_the_next()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna", note: "Hej Anna"), Person("bo"));
        var desk = new Desk("s", "n");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Equal(["Hej Anna"], desk.Copied);
        Assert.Equal([Link + "anna"], desk.Opened);
        Assert.Contains("0 of 15 today, 0 of 80 in 7 days. 2 waiting.", desk.Text);
        Assert.Contains("Note copied (8 characters).", desk.Text);

        var data = await Read(ledger);
        Assert.Equal(PersonState.MarkedSent, data.Find("anna")!.State);
        Assert.Equal(Now, data.Find("anna")!.MarkedSentAt);
        Assert.Equal(PersonState.Waiting, data.Find("bo")!.State);
    }

    [Fact]
    public async Task Each_answer_moves_people_along_in_list_order()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("a"), Person("b"), Person("c"));
        var desk = new Desk("k", "y", "n", "", "s");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        var data = await Read(ledger);
        Assert.Equal(PersonState.Skipped, data.Find("a")!.State);
        Assert.Equal(PersonState.Never, data.Find("b")!.State);
        Assert.Equal(PersonState.MarkedSent, data.Find("c")!.State);
        Assert.Equal(1, OutreachCaps.Count(data, Now).Today);
        Assert.Contains("Nobody is waiting.", desk.Text);
    }

    [Theory]
    [InlineData("q")]
    [InlineData(null)]
    public async Task Quitting_or_the_input_ending_leaves_the_person_waiting(string? answer)
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna"));

        await new Desk(answer).Session(ledger).RunAsync(CancellationToken.None);

        var anna = (await Read(ledger)).Find("anna")!;
        Assert.Equal(PersonState.Waiting, anna.State);
        Assert.Null(anna.MarkedSentAt);
        Assert.Equal(Now, anna.OpenedAt);
    }

    [Fact]
    public async Task A_wrong_key_asks_again_and_records_nothing()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna"));

        var desk = new Desk("x", "", "yes", "s");
        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Equal(4, desk.Output.Count(line => line.StartsWith("[s]ent")));
        Assert.Equal(PersonState.MarkedSent, (await Read(ledger)).Find("anna")!.State);
    }

    [Fact]
    public async Task A_profile_opened_before_gets_a_warning()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna"));

        await new Desk("q").Session(ledger).RunAsync(CancellationToken.None);

        var second = new Desk("q");
        await second.Session(ledger, Now.AddHours(1)).RunAsync(CancellationToken.None);

        Assert.Contains("hasn't already gone out", second.Text);
    }

    [Fact]
    public async Task Without_a_note_nothing_is_copied_and_the_profile_still_opens()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna", note: null));
        var desk = new Desk("q");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Empty(desk.Copied);
        Assert.Single(desk.Opened);
        Assert.Contains("No note", desk.Text);
    }

    [Fact]
    public async Task A_failed_copy_or_open_says_what_to_do_instead()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna", note: "Hej"));
        var desk = new Desk("q") { CopyWorks = false, OpenWorks = false };

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Contains("copy it from above", desk.Text);
        Assert.Contains("use the link above", desk.Text);
        Assert.Contains("Hej", desk.Output);
        Assert.Contains(desk.Output, line => line.Contains(Link + "anna"));
    }

    [Fact]
    public async Task Text_from_a_list_cannot_send_escape_sequences_to_the_terminal()
    {
        using var dir = new TempDir();
        var person = Person("anna", note: "Hi\u001b[2Jthere\nline two");
        person.FirstName = "An\u0007na";
        var ledger = await Ledger(dir, person);
        var desk = new Desk("q");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(desk.Output, line => line.Contains('\u001b') || line.Contains('\u0007'));
        Assert.Contains("Hi[2Jthere\nline two", desk.Output);
    }

    // When a session refuses

    [Fact]
    public async Task The_daily_cap_stops_before_opening_anyone()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, [.. Sent(OutreachCaps.Daily, Now), Person("anna")]);
        var desk = new Desk("s");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Empty(desk.Opened);
        Assert.Contains("daily cap of 15", desk.Text);
        Assert.Equal(PersonState.Waiting, (await Read(ledger)).Find("anna")!.State);
    }

    [Fact]
    public async Task Reaching_the_daily_cap_ends_the_session_without_asking()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, [.. Sent(OutreachCaps.Daily - 1, Now), Person("anna"), Person("bo")]);
        var desk = new Desk("s", "y", "s");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Equal([Link + "anna"], desk.Opened);
        Assert.Contains("daily cap of 15", desk.Text);
        Assert.Equal(PersonState.Waiting, (await Read(ledger)).Find("bo")!.State);
    }

    [Fact]
    public async Task The_weekly_cap_stops_a_new_day_too()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, [.. Sent(OutreachCaps.Weekly, Now.AddDays(-2)), Person("anna")]);
        var desk = new Desk("s");

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Empty(desk.Opened);
        Assert.Contains("weekly cap of 80", desk.Text);
    }

    [Fact]
    public async Task A_pause_holds_until_its_date_and_then_lets_go()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna"));
        await ledger.UpdateAsync(data => data.PausedUntil = Now.AddDays(3), CancellationToken.None);

        var paused = new Desk("q");
        await paused.Session(ledger).RunAsync(CancellationToken.None);
        Assert.Empty(paused.Opened);
        Assert.Contains("Paused until", paused.Text);

        var later = new Desk("q");
        await later.Session(ledger, Now.AddDays(4)).RunAsync(CancellationToken.None);
        Assert.Single(later.Opened);
    }

    [Theory]
    [InlineData(false, "The list is empty")]
    [InlineData(true, "Nobody is waiting")]
    public async Task An_empty_list_says_so(bool listed, string message)
    {
        using var dir = new TempDir();
        var ledger = listed ? await Ledger(dir, Person("anna", PersonState.Skipped)) : await Ledger(dir);
        var desk = new Desk();

        await desk.Session(ledger).RunAsync(CancellationToken.None);

        Assert.Contains(message, desk.Text);
    }

    [Fact]
    public async Task A_never_set_during_a_session_stands()
    {
        using var dir = new TempDir();
        var ledger = await Ledger(dir, Person("anna"));

        var session = new OutreachSession(
            ledger,
            () => Now,
            () =>
            {
                // Another terminal marks them while this one waits at the prompt.
                ledger.UpdateAsync(data => data.Find("anna")!.State = PersonState.Never, CancellationToken.None).GetAwaiter().GetResult();
                return "s";
            },
            _ => { },
            _ => true,
            _ => true);

        await session.RunAsync(CancellationToken.None);

        var anna = (await Read(ledger)).Find("anna")!;
        Assert.Equal(PersonState.Never, anna.State);
        Assert.Equal(Now, anna.MarkedSentAt);
    }
}
