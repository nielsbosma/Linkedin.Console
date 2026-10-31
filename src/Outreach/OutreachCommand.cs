using System.ComponentModel;
using Linkedin.Console.Infrastructure;
using Spectre.Console.Cli;

namespace Linkedin.Console.Outreach;

public class OutreachSettings : OutputSettings
{
    [CommandOption("--dir <DIR>")]
    [Description("Folder that holds the list. It contains names and messages, so keep it private")]
    public required string Dir { get; init; }
}

/// <summary>A command that works on the list. None of them talks to LinkedIn.</summary>
public abstract class OutreachCommand<TSettings> : LinkedInCommand<TSettings> where TSettings : OutreachSettings
{
    protected sealed override void Check(TSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Dir)) throw LinkedInException.Invalid("--dir is required.");
        CheckOptions(settings);
    }

    protected virtual void CheckOptions(TSettings settings) { }

    protected sealed override Task<object?> RunAsync(TSettings settings, CancellationToken ct) =>
        RunAsync(new OutreachLedger(Path.GetFullPath(settings.Dir)), settings, ct);

    protected abstract Task<object?> RunAsync(OutreachLedger ledger, TSettings settings, CancellationToken ct);

    protected static string Time(DateTimeOffset? at) => at?.ToString("u") ?? "";
}
