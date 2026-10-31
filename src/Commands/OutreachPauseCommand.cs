using System.ComponentModel;
using System.Globalization;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Outreach;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class OutreachPauseCommand : OutreachCommand<OutreachPauseCommand.Settings>
{
    public sealed class Settings : OutreachSettings
    {
        [CommandOption("--until <DATE>")]
        [Description("outreach next refuses until this date (yyyy-MM-dd)")]
        public string? Until { get; init; }

        [CommandOption("--clear")]
        [Description("End a pause now")]
        public bool Clear { get; init; }
    }

    protected override void CheckOptions(Settings settings)
    {
        if (settings.Clear == settings.Until is not null)
            throw LinkedInException.Invalid("Use either --until DATE or --clear.");

        if (settings.Until is not null && ParseDate(settings.Until) <= DateTimeOffset.UtcNow)
            throw LinkedInException.Invalid("--until has to be in the future. To end a pause, use --clear.");
    }

    protected override async Task<object?> RunAsync(OutreachLedger ledger, Settings settings, CancellationToken ct)
    {
        DateTimeOffset? until = settings.Until is null ? null : ParseDate(settings.Until);
        await ledger.UpdateAsync(data => data.PausedUntil = until, ct);

        return new Dictionary<string, object?> { ["pausedUntil"] = until is null ? "none" : Time(until) };
    }

    /// <summary>The start of that day where the user is.</summary>
    private static DateTimeOffset ParseDate(string text)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw LinkedInException.Invalid($"'{text}' is not a date. Use yyyy-MM-dd.");

        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
