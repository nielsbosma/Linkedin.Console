using System.Text.Json;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Storage;

namespace Linkedin.Console.Auth;

/// <summary>
/// A pasted token does not say when it expires, so <see cref="ExpiresAt"/> is the paste date plus
/// LinkedIn's 60 days and is labelled as an estimate wherever it is shown.
/// </summary>
public sealed record StoredToken(string AccessToken, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt, bool ExpiryEstimated)
{
    public const int LifetimeDays = 60;

    public static StoredToken Pasted(string accessToken, DateTimeOffset now) =>
        new(accessToken, now, now.AddDays(LifetimeDays), true);

    public int DaysLeft(DateTimeOffset now) => (int)Math.Floor((ExpiresAt - now).TotalDays);
}

public static class TokenStore
{
    /// <summary>One login per user today; the name leaves room for accounts later.</summary>
    private const string Key = "token:default";

    public static void Save(StoredToken token) =>
        SecretStoreFactory.Create().Set(Key, JsonSerializer.Serialize(token));

    public static StoredToken? Load()
    {
        var value = SecretStoreFactory.Create().Get(Key);
        return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<StoredToken>(value);
    }

    public static void Delete() => SecretStoreFactory.Create().Delete(Key);
}

public sealed record ResolvedToken(string Token, string Source, StoredToken? Stored);

public static class TokenResolver
{
    public const string EnvironmentVariable = "LINKEDIN_ACCESS_TOKEN";

    private const int WarnDays = 7;

    public static ResolvedToken Resolve(string? option)
    {
        if (!string.IsNullOrWhiteSpace(option))
            return new ResolvedToken(option.Trim(), "--access-token", null);

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            return new ResolvedToken(env.Trim(), EnvironmentVariable, null);

        var stored = TokenStore.Load()
            ?? throw LinkedInException.Auth(
                "No LinkedIn token is configured.",
                "These commands use your own LinkedIn app and token.",
                "linkedin setup");

        var daysLeft = stored.DaysLeft(DateTimeOffset.UtcNow);
        if (daysLeft <= WarnDays)
            OutputHelper.Status(daysLeft < 0
                ? $"warning: your LinkedIn token probably expired on {stored.ExpiresAt:yyyy-MM-dd}. Create a new one, then run: linkedin auth token"
                : $"warning: your LinkedIn token expires in about {daysLeft} days ({stored.ExpiresAt:yyyy-MM-dd}). Create a new one, then run: linkedin auth token");

        return new ResolvedToken(stored.AccessToken, "secret store", stored);
    }
}
