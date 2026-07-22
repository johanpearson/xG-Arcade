using System.Net;
using System.Text;

namespace XGArcade.Core.Tests.Auth;

// Same minimal test double as XGArcade.DataSync.Tests/FakeHttpMessageHandler.cs
// (that one is `internal` to its own assembly, so it isn't reusable from
// here) -- stands in for Supabase Auth's REST API so
// SupabaseAuthClientCaptchaTests can exercise SupabaseAuthClient's own
// request/response handling (ReadFailureResultAsync's IsCaptchaRejection
// detection) without a mocking framework (docs/coding-guidelines.md) or a
// real network call.
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await responder(request, cancellationToken);
    }

    public static FakeHttpMessageHandler ReturningJson(HttpStatusCode statusCode, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));
}
