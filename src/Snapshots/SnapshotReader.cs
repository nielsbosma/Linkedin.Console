using System.Runtime.CompilerServices;
using Linkedin.Console.Infrastructure;

namespace Linkedin.Console.Snapshots;

public static class SnapshotReader
{
    /// <summary>Only there so an API that starts ignoring <c>start</c> cannot loop forever.</summary>
    private const int MaxPages = 100_000;

    /// <summary>
    /// Every element of a domain, or with no domain of everything LinkedIn holds, until the 404 that
    /// ends the pages. Page sizes vary (616 rows in one measured page), so nothing assumes one.
    /// </summary>
    public static async IAsyncEnumerable<SnapshotElement> ReadAsync(
        LinkedInApiClient client, string? domain, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var start = 0; start < MaxPages; start++)
        {
            var page = await client.GetSnapshotPageAsync(domain, start, ct);
            if (page is null || page.Count == 0) yield break;

            foreach (var element in page) yield return element;
        }

        throw new LinkedInException(ErrorCode.Error, $"LinkedIn was still returning pages after {MaxPages}. Stopped.");
    }
}
