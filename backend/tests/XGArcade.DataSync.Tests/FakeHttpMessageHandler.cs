namespace XGArcade.DataSync.Tests;

// Stands in for the real Wikidata Query Service endpoint in tests — the
// "mocked HTTP" the S-006 backlog acceptance criteria calls for, without a
// mocking framework dependency this project doesn't otherwise need.
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return responder(request, cancellationToken);
    }

    public static FakeHttpMessageHandler ReturningJson(string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/sparql-results+json"),
        }));

    public static FakeHttpMessageHandler ReturningStatus(System.Net.HttpStatusCode statusCode) =>
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
