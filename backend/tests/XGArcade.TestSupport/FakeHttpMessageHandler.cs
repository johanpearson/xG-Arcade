using System.Net;
using System.Text;

namespace XGArcade.TestSupport;

// Shared test double standing in for whatever real HTTP endpoint a client
// under test talks to (Wikidata Query Service, Supabase Auth's REST API,
// ...) -- promoted out of XGArcade.DataSync.Tests and XGArcade.Core.Tests
// once it had drifted into two verbatim-but-diverging copies. No mocking
// framework (docs/coding-guidelines.md); this project is a plain class
// library referenced by both test projects' .csproj, not an NUnit test
// project itself.
public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await responder(request, cancellationToken);
    }

    // Defaults to the Wikidata Query Service's SPARQL results content type —
    // preserves XGArcade.DataSync.Tests's existing call sites unchanged.
    public static FakeHttpMessageHandler ReturningJson(string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/sparql-results+json"),
        }));

    public static FakeHttpMessageHandler ReturningJson(HttpStatusCode statusCode, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    public static FakeHttpMessageHandler ReturningStatus(HttpStatusCode statusCode) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));

    // Never completes on its own — only resolves by the caller's timeout
    // cancelling the token, letting a test exercise the timeout path
    // without waiting out a real multi-second delay.
    public static FakeHttpMessageHandler NeverResponding() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
}
