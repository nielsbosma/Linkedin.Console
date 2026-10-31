using System.ComponentModel;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Outreach;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class OutreachNeverCommand : OutreachCommand<OutreachNeverCommand.Settings>
{
    public sealed class Settings : OutreachSettings
    {
        [CommandArgument(0, "<URL>")]
        [Description("The person's LinkedIn profile link")]
        public required string Url { get; init; }
    }

    protected override async Task<object?> RunAsync(OutreachLedger ledger, Settings settings, CancellationToken ct)
    {
        string url, key;
        try
        {
            (url, key) = LinkedInUrl.CanonicalProfile(settings.Url);
        }
        catch (ArgumentException)
        {
            throw LinkedInException.Invalid($"{settings.Url} is not a LinkedIn profile link.");
        }

        var wasListed = await ledger.UpdateAsync(data =>
        {
            var person = data.Find(key);
            if (person is not null)
            {
                person.State = PersonState.Never;
                return true;
            }

            // Listed even when new, so a later outreach add can't bring them back.
            data.People.Add(new OutreachPerson { Key = key, Url = url, State = PersonState.Never, AddedAt = DateTimeOffset.UtcNow });
            return false;
        }, ct);

        return new Dictionary<string, object?>
        {
            ["url"] = url,
            ["state"] = OutreachLedger.StateName(PersonState.Never),
            ["wasListed"] = wasListed
        };
    }
}
