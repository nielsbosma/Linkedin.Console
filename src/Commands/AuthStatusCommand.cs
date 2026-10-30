using Linkedin.Console.Auth;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;

namespace Linkedin.Console.Commands;

public sealed class AuthStatusCommand : LinkedInCommand<MemberSettings>
{
    protected override async Task<object?> RunAsync(MemberSettings settings, CancellationToken ct)
    {
        var token = TokenResolver.Resolve(settings.AccessToken);
        using var client = new LinkedInApiClient(token.Token);
        var changeLogging = await client.IsChangeLoggingOnAsync(ct);

        var result = new Dictionary<string, object?>
        {
            ["status"] = "valid",
            ["source"] = token.Source,
            ["changeLogging"] = changeLogging ? "on" : "off"
        };

        if (token.Stored is { } stored)
        {
            result["secretStore"] = SecretStoreFactory.Create().Name;
            result["storedAt"] = stored.StoredAt.ToString("yyyy-MM-dd");
            result["expiresAt"] = stored.ExpiresAt.ToString("yyyy-MM-dd");
            result["expiryEstimated"] = stored.ExpiryEstimated;
            result["daysLeft"] = stored.DaysLeft(DateTimeOffset.UtcNow);
        }
        return result;
    }
}
