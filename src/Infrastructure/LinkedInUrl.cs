using System.Text.RegularExpressions;

namespace Linkedin.Console.Infrastructure;

public static partial class LinkedInUrl
{
    public static string NormalizeProfile(string url)
    {
        var match = ProfileRegex().Match(url.Trim());

        if (!match.Success)
            throw new ArgumentException($"Invalid LinkedIn profile URL: {url}");

        return $"https://www.linkedin.com/in/{match.Groups[1].Value}";
    }

    /// <summary>
    /// A link built here rather than taken from the input, as it gets opened in a browser, and the
    /// handle in lower case for comparing. Obfuscated <c>ACoAA…</c> handles need their case in the link.
    /// </summary>
    public static (string Url, string Key) CanonicalProfile(string url)
    {
        var match = ProfileRegex().Match(url.Trim());
        var handle = match.Success ? Uri.UnescapeDataString(match.Groups[1].Value.Split('#')[0]) : "";

        if (string.IsNullOrWhiteSpace(handle))
            throw new ArgumentException($"Invalid LinkedIn profile URL: {url}");

        return ($"https://www.linkedin.com/in/{Uri.EscapeDataString(handle)}", handle.ToLowerInvariant());
    }

    public static string NormalizePost(string url)
    {
        var trimmed = url.Trim();

        if (PostUrlRegex().IsMatch(trimmed) || ProfileRegex().IsMatch(trimmed) || CompanyRegex().IsMatch(trimmed) || SearchRegex().IsMatch(trimmed))
            return trimmed;

        throw new ArgumentException($"Invalid LinkedIn URL: {url}. Expected a post, profile, company, or search URL.");
    }

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?linkedin\.com/in/([^/?\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileRegex();

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?linkedin\.com/(feed/update|posts/)", RegexOptions.IgnoreCase)]
    private static partial Regex PostUrlRegex();

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?linkedin\.com/company/", RegexOptions.IgnoreCase)]
    private static partial Regex CompanyRegex();

    [GeneratedRegex(@"(?:https?://)?(?:www\.)?linkedin\.com/search/", RegexOptions.IgnoreCase)]
    private static partial Regex SearchRegex();
}
