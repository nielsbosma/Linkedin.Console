using System.ComponentModel;
using System.Text;
using Linkedin.Console.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linkedin.Console.Infrastructure;

public class OutputSettings : CommandSettings
{
    [CommandOption("--format <FORMAT>")]
    [Description("Output format: yaml (default) or json")]
    public string Format { get; init; } = "yaml";

    public override ValidationResult Validate() =>
        Format.Equals("yaml", StringComparison.OrdinalIgnoreCase) || Format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Success()
            : ValidationResult.Error("--format must be yaml or json");
}

/// <summary>Kept apart from <see cref="GlobalSettings"/>, which carries the Apify key.</summary>
public class MemberSettings : OutputSettings
{
    [CommandOption("--access-token <TOKEN>")]
    [Description("LinkedIn access token (or set LINKEDIN_ACCESS_TOKEN, or run 'linkedin auth token')")]
    public string? AccessToken { get; init; }
}

/// <summary>
/// Every command funnels its failures through one place, so the error envelope and the exit
/// code always agree. Returning null from <see cref="RunAsync"/> means "no stdout payload".
/// </summary>
public abstract class LinkedInCommand<TSettings> : AsyncCommand<TSettings> where TSettings : OutputSettings
{
    protected sealed override async Task<int> ExecuteAsync(
        CommandContext context, TSettings settings, CancellationToken cancellation)
    {
        UseUtf8WhenRedirected();

        try
        {
            Check(settings);
            var result = await RunAsync(settings, cancellation);
            if (result is not null) OutputHelper.Write(result, settings.Format);
            return 0;
        }
        catch (LinkedInException ex)
        {
            OutputHelper.WriteError(ex, settings.Format);
            return (int)ex.Code;
        }
        catch (HttpRequestException ex)
        {
            OutputHelper.WriteError(new LinkedInException(ErrorCode.Network, "Could not reach LinkedIn.", ex.Message), settings.Format);
            return (int)ErrorCode.Network;
        }
        catch (TaskCanceledException)
        {
            OutputHelper.WriteError(new LinkedInException(ErrorCode.Network, "The request to LinkedIn timed out or was cancelled."), settings.Format);
            return (int)ErrorCode.Network;
        }
        catch (Exception ex)
        {
            OutputHelper.WriteError(new LinkedInException(ErrorCode.Error, ex.Message, ex.GetType().Name), settings.Format);
            return (int)ErrorCode.Error;
        }
    }

    /// <summary>
    /// A redirected stream otherwise follows the console's code page: on 850 a name with an unusual
    /// letter made the JSON unparseable. A terminal keeps its own encoding, which it can display.
    /// </summary>
    private static void UseUtf8WhenRedirected()
    {
        var utf8 = new UTF8Encoding(false);
        if (System.Console.IsOutputRedirected)
            System.Console.SetOut(new StreamWriter(System.Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        if (System.Console.IsErrorRedirected)
            System.Console.SetError(new StreamWriter(System.Console.OpenStandardError(), utf8) { AutoFlush = true });
    }

    /// <summary>
    /// Option checks, ahead of any token lookup. Not Spectre's Validate: that one prints to stdout
    /// and exits -1, which an agent cannot tell apart from a crash.
    /// </summary>
    protected virtual void Check(TSettings settings) { }

    protected abstract Task<object?> RunAsync(TSettings settings, CancellationToken ct);
}

/// <summary>A command that reads the member's data: resolves the token and opens a client.</summary>
public abstract class MemberCommand<TSettings> : LinkedInCommand<TSettings> where TSettings : MemberSettings
{
    protected sealed override async Task<object?> RunAsync(TSettings settings, CancellationToken ct)
    {
        var token = TokenResolver.Resolve(settings.AccessToken);
        using var client = new LinkedInApiClient(token.Token);
        return await RunAsync(client, settings, ct);
    }

    protected abstract Task<object?> RunAsync(LinkedInApiClient client, TSettings settings, CancellationToken ct);
}
