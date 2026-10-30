using System.ComponentModel;
using Linkedin.Console.Auth;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class AuthTokenCommand : LinkedInCommand<AuthTokenCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("--stdin")]
        [Description("Read the token from stdin instead of a hidden prompt")]
        public bool Stdin { get; init; }
    }

    protected override async Task<object?> RunAsync(Settings settings, CancellationToken ct)
    {
        var token = Normalize(await ReadTokenAsync(settings.Stdin, ct));

        using var client = new LinkedInApiClient(token);

        // Checked before anything is stored, so a mistyped token never replaces a working one.
        var changeLogging = await client.IsChangeLoggingOnAsync(ct);
        if (!changeLogging) await client.EnableChangeLoggingAsync(ct);

        var stored = StoredToken.Pasted(token, DateTimeOffset.UtcNow);
        TokenStore.Save(stored);

        return new Dictionary<string, object?>
        {
            ["status"] = "stored",
            ["secretStore"] = SecretStoreFactory.Create().Name,
            ["expiresAt"] = stored.ExpiresAt.ToString("yyyy-MM-dd"),
            ["expiryEstimated"] = stored.ExpiryEstimated,
            ["changeLogging"] = changeLogging ? "already on" : "turned on"
        };
    }

    private static async Task<string> ReadTokenAsync(bool stdin, CancellationToken ct)
    {
        if (stdin) return await System.Console.In.ReadToEndAsync(ct);

        if (System.Console.IsInputRedirected)
            throw LinkedInException.Invalid("No terminal to prompt in.", "Pipe the token in and pass --stdin.");

        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) });
        return console.Prompt(new TextPrompt<string>("LinkedIn [green]access token[/]:").Secret());
    }

    private static string Normalize(string input)
    {
        var token = input.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..].Trim();

        if (token.Length == 0)
            throw LinkedInException.Invalid("The token is empty.", "linkedin setup");
        if (token.Any(char.IsWhiteSpace))
            throw LinkedInException.Invalid("The token contains whitespace, so it was probably not copied whole.");

        return token;
    }
}
