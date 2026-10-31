using Linkedin.Console.Infrastructure;
using Linkedin.Console.Outreach;

namespace Linkedin.Console.Commands;

public sealed class OutreachNextCommand : OutreachCommand<OutreachSettings>
{
    /// <summary>The point of assisted mode is that a person goes through the list, so a script can't.</summary>
    protected override void CheckOptions(OutreachSettings settings)
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            throw LinkedInException.Invalid(
                "outreach next needs a person at a terminal: you press Send on LinkedIn, then say so here.",
                "Run it in a terminal yourself. An agent can use outreach add and outreach list.");
    }

    protected override async Task<object?> RunAsync(OutreachLedger ledger, OutreachSettings settings, CancellationToken ct)
    {
        var session = new OutreachSession(
            ledger, () => DateTimeOffset.UtcNow, System.Console.ReadLine, System.Console.WriteLine, Desktop.Copy, Desktop.Open);

        await session.RunAsync(ct);
        return null;
    }
}
