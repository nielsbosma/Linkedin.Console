using Linkedin.Console.Auth;
using Linkedin.Console.Infrastructure;

namespace Linkedin.Console.Commands;

public sealed class AuthLogoutCommand : LinkedInCommand<OutputSettings>
{
    protected override Task<object?> RunAsync(OutputSettings settings, CancellationToken ct)
    {
        var hadToken = TokenStore.Load() is not null;
        TokenStore.Delete();

        return Task.FromResult<object?>(new Dictionary<string, object?>
        {
            ["status"] = hadToken ? "removed" : "nothing stored",
            ["note"] = hadToken
                ? "The token stays valid on LinkedIn's side until it expires. This only removed the copy on this computer."
                : null,
            ["warning"] = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenResolver.EnvironmentVariable))
                ? null
                : $"{TokenResolver.EnvironmentVariable} is still set, and commands will keep using it."
        });
    }
}
