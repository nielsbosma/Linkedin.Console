using System.Net;
using System.Text;
using Linkedin.Console.Infrastructure;

namespace Linkedin.Console.Tests;

/// <summary>Answers from a function of the request, and records what was asked.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        var (status, body) = respond(request);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    public LinkedInApiClient Client() => new("test-token", this, (_, _) => Task.CompletedTask);
}

/// <summary>A folder that exists for one test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("linkedin-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
