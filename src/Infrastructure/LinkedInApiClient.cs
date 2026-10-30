using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkedin.Console.Infrastructure;

/// <summary>One element of a snapshot page: a domain and some of its rows.</summary>
public sealed record SnapshotElement(string Domain, List<object?> Rows);

/// <summary>
/// LinkedIn's Member Data Portability API. All headers, retries and error messages live here.
/// Response bodies of successful calls are the member's private data, so they are never put into
/// an exception or a status line; only error bodies are, and only after <see cref="Clean"/>.
/// </summary>
public sealed partial class LinkedInApiClient : IDisposable
{
    /// <summary>202401 answered 426 on 2026-10-30, and a missing header 400.</summary>
    public const string ApiVersion = "202312";

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public LinkedInApiClient(string token, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _token = token;
        _delay = delay ?? Task.Delay;
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri("https://api.linkedin.com/rest/"),
            Timeout = TimeSpan.FromSeconds(90)
        };
    }

    /// <summary>
    /// One page, where <paramref name="start"/> counts pages rather than rows. Null is the 404 that
    /// ends the pages: on the first page it means the domain is empty or not ready, and LinkedIn
    /// gives no way to tell those two apart. The status is what counts, as the wording differs from the docs.
    /// </summary>
    public async Task<IReadOnlyList<SnapshotElement>?> GetSnapshotPageAsync(string? domain, int start, CancellationToken ct)
    {
        var path = "memberSnapshotData?q=criteria"
                   + (domain is null ? "" : "&domain=" + Uri.EscapeDataString(domain))
                   + "&start=" + start;

        using var response = await SendAsync(HttpMethod.Get, path, null, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        if (response.StatusCode == HttpStatusCode.BadRequest && domain is not null)
            throw LinkedInException.Invalid(
                $"Domain '{domain}' is not supported by LinkedIn.",
                "linkedin me domains --available");

        await EnsureSuccessAsync(response, ct);

        using var doc = await ReadJsonAsync(response, ct);
        var elements = new List<SnapshotElement>();
        if (!doc.RootElement.TryGetProperty("elements", out var items)) return elements;

        foreach (var element in items.EnumerateArray())
        {
            var name = element.TryGetProperty("snapshotDomain", out var d) ? d.GetString() : null;
            if (name is null || !IsDomainName(name))
                throw new LinkedInException(ErrorCode.Error, "LinkedIn returned a snapshot page without a valid domain name.");

            var rows = element.TryGetProperty("snapshotData", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(YamlOutput.JsonToObject).ToList()
                : [];
            elements.Add(new SnapshotElement(name, rows));
        }
        return elements;
    }

    public async Task<IReadOnlyList<JsonElement>> GetChangeLogsAsync(long? startTime, int count, CancellationToken ct)
    {
        var path = "memberChangeLogs?q=memberAndApplication&count=" + count
                   + (startTime is null ? "" : "&startTime=" + startTime);

        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        await EnsureSuccessAsync(response, ct);

        using var doc = await ReadJsonAsync(response, ct);
        return doc.RootElement.TryGetProperty("elements", out var items)
            ? items.EnumerateArray().Select(e => e.Clone()).ToList()
            : [];
    }

    /// <summary>Also the cheapest way to check a token: one call that returns no member data.</summary>
    public async Task<bool> IsChangeLoggingOnAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "memberAuthorizations?q=memberAndApplication", null, ct);
        await EnsureSuccessAsync(response, ct);

        using var doc = await ReadJsonAsync(response, ct);
        return doc.RootElement.TryGetProperty("elements", out var items) && items.GetArrayLength() > 0;
    }

    /// <summary>409 means it was already on, which is what the caller wanted.</summary>
    public async Task EnableChangeLoggingAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "memberAuthorizations", "{}", ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return;
        await EnsureSuccessAsync(response, ct);
    }

    public static bool IsDomainName(string name) => DomainNamePattern().IsMatch(name);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("Linkedin-Version", ApiVersion);
            // LinkedIn's examples send it on GETs as well.
            request.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request, ct);

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retryable || attempt >= Backoff.Length) return response;

            var wait = response.Headers.RetryAfter?.Delta is { } delta && delta < MaxRetryAfter ? delta : Backoff[attempt];
            response.Dispose();
            await _delay(wait, ct);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var status = (int)response.StatusCode;
        var detail = $"HTTP {status}: {Clean(await response.Content.ReadAsStringAsync(ct))}";

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => LinkedInException.Auth(
                "LinkedIn rejected the token, or it has expired.", detail,
                "Create a new token (see: linkedin setup), then run: linkedin auth token"),
            HttpStatusCode.Forbidden => LinkedInException.Auth(
                "This token has no access to the Member Data Portability API.", detail,
                "Check that your app has the product and that the token has its scope (see: linkedin setup). LinkedIn only offers it to members in the EEA and Switzerland."),
            HttpStatusCode.TooManyRequests => new LinkedInException(
                ErrorCode.RateLimited, "LinkedIn's daily limit was reached.", detail,
                "It resets at midnight UTC. Run the same command again after that to continue."),
            HttpStatusCode.UpgradeRequired => new LinkedInException(
                ErrorCode.Error, $"LinkedIn no longer accepts API version {ApiVersion}, or refuses it for this call.", detail,
                "Update the tool: dotnet tool update -g Linkedin.Console"),
            HttpStatusCode.BadRequest => new LinkedInException(ErrorCode.InvalidInput, "LinkedIn rejected the request.", detail),
            HttpStatusCode.NotFound => new LinkedInException(ErrorCode.NotFound, "LinkedIn found nothing at this address.", detail),
            _ => new LinkedInException(ErrorCode.Error, $"LinkedIn returned HTTP {status}.", detail)
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>Error bodies name the member's URN; that has no place in a log.</summary>
    private static string Clean(string body)
    {
        var text = UrnPattern().Replace(body, "<urn>");
        return text.Length > 300 ? text[..300] + "..." : text;
    }

    /// <summary>Mixed case is real: LinkedIn lists one domain as <c>Events</c>.</summary>
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex DomainNamePattern();

    [GeneratedRegex("""urn:li:[^\s",}]+""")]
    private static partial Regex UrnPattern();

    public void Dispose() => _http.Dispose();
}
