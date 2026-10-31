using System.ComponentModel;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Outreach;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class OutreachListCommand : OutreachCommand<OutreachListCommand.Settings>
{
    public sealed class Settings : OutreachSettings
    {
        [CommandOption("--state <STATE>")]
        [Description("Only people in this state: waiting, markedSent, skipped or never")]
        public string? State { get; init; }
    }

    protected override void CheckOptions(Settings settings)
    {
        if (settings.State is not null && !TryParse(settings.State, out _))
            throw LinkedInException.Invalid($"--state must be one of: {string.Join(", ", Enum.GetValues<PersonState>().Select(OutreachLedger.StateName))}.");
    }

    protected override async Task<object?> RunAsync(OutreachLedger ledger, Settings settings, CancellationToken ct)
    {
        var data = await ledger.ReadAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var caps = OutreachCaps.Count(data, now);

        PersonState? filter = settings.State is not null && TryParse(settings.State, out var state) ? state : null;

        return new Dictionary<string, object?>
        {
            ["counts"] = Enum.GetValues<PersonState>().ToDictionary(
                OutreachLedger.StateName, s => (object?)data.People.Count(p => p.State == s)),
            ["sentToday"] = $"{caps.Today} of {OutreachCaps.Daily}",
            ["sentInSevenDays"] = $"{caps.LastSevenDays} of {OutreachCaps.Weekly}",
            ["pausedUntil"] = data.PausedUntil is { } until && until > now ? Time(until) : null,
            ["people"] = data.People.Where(p => filter is null || p.State == filter).Select(Row).ToList()
        };
    }

    private static Dictionary<string, object?> Row(OutreachPerson person) => new()
    {
        ["url"] = person.Url,
        ["firstName"] = person.FirstName,
        ["state"] = OutreachLedger.StateName(person.State),
        ["note"] = person.Note,
        ["followup"] = person.Followup,
        ["addedAt"] = Time(person.AddedAt),
        ["openedAt"] = person.OpenedAt is null ? null : Time(person.OpenedAt),
        ["markedSentAt"] = person.MarkedSentAt is null ? null : Time(person.MarkedSentAt)
    };

    private static bool TryParse(string text, out PersonState state) =>
        Enum.TryParse(text, ignoreCase: true, out state) && Enum.IsDefined(state);
}
