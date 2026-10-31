using System.ComponentModel;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Outreach;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class OutreachAddCommand : OutreachCommand<OutreachAddCommand.Settings>
{
    public sealed class Settings : OutreachSettings
    {
        [CommandOption("--file <FILE>")]
        [Description("CSV with the columns url, firstName, note and followup. Only url is required")]
        public required string File { get; init; }
    }

    protected override void CheckOptions(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.File)) throw LinkedInException.Invalid("--file is required.");
        if (!System.IO.File.Exists(settings.File))
            throw new LinkedInException(ErrorCode.NotFound, $"There is no file at {settings.File}.");
    }

    protected override async Task<object?> RunAsync(OutreachLedger ledger, Settings settings, CancellationToken ct)
    {
        var people = OutreachCsv.Parse(await System.IO.File.ReadAllTextAsync(settings.File, ct));
        var result = await ledger.UpdateAsync(data => OutreachCsv.AddTo(data, people, DateTimeOffset.UtcNow), ct);

        if (result.OverReportedFreeLimit > 0)
            OutputHelper.Status($"warning: {result.OverReportedFreeLimit} notes are over {OutreachCsv.FreeNoteLimit} characters, the limit reported for free LinkedIn accounts.");
        if (result.Obfuscated > 0)
            OutputHelper.Status($"warning: {result.Obfuscated} links use LinkedIn's /in/ACoAA… form instead of a handle, so they may not match LinkedIn's own data later.");

        return new Dictionary<string, object?>
        {
            ["added"] = result.Added,
            ["alreadyListed"] = result.AlreadyListed,
            ["withoutNote"] = result.WithoutNote
        };
    }
}
