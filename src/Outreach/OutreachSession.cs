using System.Globalization;

namespace Linkedin.Console.Outreach;

/// <summary>
/// One sitting of <c>outreach next</c>: copy a note, open a profile, and record what the person says
/// they did. Nothing here touches LinkedIn. Only an answer from the keyboard moves someone along, so
/// the ledger holds what a person said, and <see cref="PersonState.MarkedSent"/> means no more than that.
/// </summary>
public sealed class OutreachSession(
    OutreachLedger ledger,
    Func<DateTimeOffset> now,
    Func<string?> readLine,
    Action<string> write,
    Func<string, bool> copy,
    Func<string, bool> open)
{
    private sealed record Offer(OutreachPerson Person, DateTimeOffset? OpenedBefore, CapStatus Caps, int Waiting);

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            string? stop = null;
            var offer = await ledger.UpdateAsync(data =>
            {
                stop = StopReason(data, now());
                return stop is null ? Take(data) : null;
            }, ct);

            if (offer is null)
            {
                write(stop!);
                return;
            }

            Show(offer);

            var state = Ask();
            if (state is null) return;

            await ledger.UpdateAsync(data => Record(data, offer.Person.Key, state.Value), ct);

            stop = StopReason(await ledger.ReadAsync(ct), now());
            if (stop is not null)
            {
                write(stop);
                return;
            }

            write("Recorded. Next person? [Y/n]");
            if (readLine()?.Trim().ToLowerInvariant() is null or "n" or "no") return;
            write("");
        }
    }

    public static string? StopReason(OutreachData data, DateTimeOffset now)
    {
        if (data.PausedUntil is { } until && until > now)
            return $"Paused until {until.ToLocalTime():yyyy-MM-dd}. To end that early: linkedin outreach pause --clear";

        if (data.People.Count == 0) return "The list is empty. Add people with: linkedin outreach add";
        if (!data.People.Exists(p => p.State == PersonState.Waiting)) return "Nobody is waiting.";

        var caps = OutreachCaps.Count(data, now);
        if (caps.DayFull) return $"That's {caps.Today} today, the daily cap of {OutreachCaps.Daily}. Come back tomorrow.";
        if (caps.WeekFull)
            return $"That's {caps.LastSevenDays} in 7 days, the weekly cap of {OutreachCaps.Weekly}. The next slot opens on {caps.NextWeeklySlot!.Value.ToLocalTime():yyyy-MM-dd HH:mm}.";

        return null;
    }

    /// <summary>The time is saved before the browser opens, so a crash or Ctrl+C still leaves the warning.</summary>
    private Offer Take(OutreachData data)
    {
        var person = data.People.First(p => p.State == PersonState.Waiting);
        var before = person.OpenedAt;
        person.OpenedAt = now();

        return new Offer(person, before, OutreachCaps.Count(data, now()), data.People.Count(p => p.State == PersonState.Waiting));
    }

    private void Show(Offer offer)
    {
        var person = offer.Person;

        write($"{offer.Caps.Today} of {OutreachCaps.Daily} today, {offer.Caps.LastSevenDays} of {OutreachCaps.Weekly} in 7 days. {offer.Waiting} waiting.");
        write("");
        write($"{Printable(person.FirstName ?? person.Key)}, {person.Url}");

        if (offer.OpenedBefore is { } before)
            write($"You opened this profile on {before.ToLocalTime():yyyy-MM-dd HH:mm} without recording anything. Check that an invitation hasn't already gone out.");

        if (person.Note is null) write("No note for this person.");
        else
        {
            write("");
            write(Printable(person.Note));
            write("");
            write(copy(person.Note)
                ? $"Note copied ({person.Note.Length.ToString(CultureInfo.InvariantCulture)} characters)."
                : "The note couldn't be copied, so copy it from above.");
        }

        write(open(person.Url) ? "Profile opened in your browser." : "The browser couldn't be opened, so use the link above.");
        write("");
    }

    /// <summary>Null means quit, which leaves the person waiting.</summary>
    private PersonState? Ask()
    {
        while (true)
        {
            write("[s]ent  s[k]ip  [n]ever contact  [q]uit without sending >");

            switch (readLine()?.Trim().ToLowerInvariant())
            {
                case null or "q" or "quit": return null;
                case "s" or "sent": return PersonState.MarkedSent;
                case "k" or "skip": return PersonState.Skipped;
                case "n" or "never": return PersonState.Never;
            }
        }
    }

    /// <summary>Someone may have run <c>outreach never</c> meanwhile, and that has to stand.</summary>
    private bool Record(OutreachData data, string key, PersonState state)
    {
        var person = data.Find(key);
        if (person is null) return false;

        if (state == PersonState.MarkedSent) person.MarkedSentAt = now();
        if (person.State == PersonState.Waiting) person.State = state;
        return true;
    }

    /// <summary>A list can come from anywhere, and a terminal acts on escape sequences it's sent.</summary>
    private static string Printable(string text) =>
        new(text.Where(c => c == '\n' || !char.IsControl(c)).ToArray());
}
