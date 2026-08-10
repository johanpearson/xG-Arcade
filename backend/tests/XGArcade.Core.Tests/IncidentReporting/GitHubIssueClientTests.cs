using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.IncidentReporting;
using XGArcade.TestSupport;

namespace XGArcade.Core.Tests.IncidentReporting;

// REQ-903/ADR-0064: GitHubIssueClient's own unit coverage — never calls the
// real GitHub API (a fake HttpMessageHandler stands in, same pattern
// SupabaseAuthClientCaptchaTests.cs already uses for SupabaseAuthClient).
public class GitHubIssueClientTests
{
    private static readonly GitHubIncidentReportOptions Options = new("johanpearson", "xg-arcade", "user-reported");

    // HttpMessageHandler, not FakeHttpMessageHandler specifically — the
    // network-failure test below passes a different HttpMessageHandler
    // subclass (FakeHttpMessageHandlerThrowingNetworkFailure), and
    // HttpClient's own constructor only needs the base type anyway.
    private static GitHubIssueClient BuildClient(HttpMessageHandler handler, string? token = "a-fine-grained-pat") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            new GitHubIncidentReportToken(token),
            Options,
            NullLogger<GitHubIssueClient>.Instance);

    [Test]
    public async Task REQ903_CreateIssueAsync_ReturnsFailure_WithoutCallingGitHub_WhenTokenIsNotConfigured()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.Created);
        var client = BuildClient(handler, token: null);

        var result = await client.CreateIssueAsync("A title", "A body", CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.IssueUrl, Is.Null);
        Assert.That(result.FailureReason, Does.Not.Contain("a-fine-grained-pat"));
        Assert.That(handler.LastRequest, Is.Null, "an unconfigured token must never send a request to GitHub at all");
    }

    [Test]
    public async Task REQ903_CreateIssueAsync_ReturnsIssueUrl_OnSuccess()
    {
        const string json = """{ "html_url": "https://github.com/johanpearson/xg-arcade/issues/42" }""";
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.Created, json);
        var client = BuildClient(handler);

        var result = await client.CreateIssueAsync("A title", "A body", CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IssueUrl, Is.EqualTo("https://github.com/johanpearson/xg-arcade/issues/42"));
    }

    [Test]
    public async Task REQ903_CreateIssueAsync_SendsBearerToken_TitleBody_AndFixedLabel()
    {
        const string json = """{ "html_url": "https://github.com/johanpearson/xg-arcade/issues/42" }""";
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.Created, json);
        var client = BuildClient(handler, token: "a-fine-grained-pat");

        await client.CreateIssueAsync("A title", "A body", CancellationToken.None);

        Assert.That(handler.LastRequest!.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(handler.LastRequest.Headers.Authorization!.Parameter, Is.EqualTo("a-fine-grained-pat"));
        Assert.That(handler.LastRequest.RequestUri!.ToString(), Is.EqualTo("https://api.github.com/repos/johanpearson/xg-arcade/issues"));
        Assert.That(handler.LastRequestBody, Does.Contain("A title"));
        Assert.That(handler.LastRequestBody, Does.Contain("A body"));
        Assert.That(handler.LastRequestBody, Does.Contain("user-reported"),
            "the label is fixed server-side (ADR-0064) — never a caller-supplied value");
    }

    [Test]
    public async Task REQ903_CreateIssueAsync_ReturnsClientSafeFailure_NeverLeakingGitHubResponseBody_OnErrorStatus()
    {
        const string json = """{ "message": "Bad credentials", "documentation_url": "https://docs.github.com" }""";
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.Unauthorized, json);
        var client = BuildClient(handler);

        var result = await client.CreateIssueAsync("A title", "A body", CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.FailureReason, Does.Not.Contain("Bad credentials"),
            "ADR-0064: GitHub's own error detail must never reach the client");
    }

    [Test]
    public async Task REQ903_CreateIssueAsync_ReturnsFailure_WhenGitHubIsUnreachable()
    {
        var handler = new FakeHttpMessageHandlerThrowingNetworkFailure();
        var client = BuildClient(handler);

        var result = await client.CreateIssueAsync("A title", "A body", CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.IssueUrl, Is.Null);
    }

    // ---- REQ-904/ADR-0066: ListOpenIssuesByLabelAsync ----------------------
    // Mirrors CreateIssueAsync's own coverage above: unconfigured-token
    // failure, non-success-status failure with server-side-only logging
    // (never a leaked GitHub error body), success parsing, and — the one
    // shape unique to this method — filtering out pull requests from
    // GitHub's issues-list response.

    [Test]
    public async Task REQ904_ListOpenIssuesByLabelAsync_ReturnsFailure_WithoutCallingGitHub_WhenTokenIsNotConfigured()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = BuildClient(handler, token: null);

        var result = await client.ListOpenIssuesByLabelAsync(CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Issues, Is.Null);
        Assert.That(result.FailureReason, Does.Not.Contain("a-fine-grained-pat"));
        Assert.That(handler.LastRequest, Is.Null, "an unconfigured token must never send a request to GitHub at all");
    }

    [Test]
    public async Task REQ904_ListOpenIssuesByLabelAsync_ReturnsFailure_ForBlankToken()
    {
        var handler = FakeHttpMessageHandler.ReturningStatus(HttpStatusCode.OK);
        var client = BuildClient(handler, token: "   ");

        var result = await client.ListOpenIssuesByLabelAsync(CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public async Task REQ904_ListOpenIssuesByLabelAsync_ReturnsIssues_OnSuccess_AndFiltersOutPullRequests()
    {
        const string json = """
        [
            { "number": 42, "title": "Grid freezes on submit", "html_url": "https://github.com/johanpearson/xg-arcade/issues/42" },
            { "number": 43, "title": "A pull request that happens to carry this label", "html_url": "https://github.com/johanpearson/xg-arcade/pull/43", "pull_request": { "url": "https://api.github.com/repos/johanpearson/xg-arcade/pulls/43" } },
            { "number": 44, "title": "Autocomplete is slow", "html_url": "https://github.com/johanpearson/xg-arcade/issues/44" }
        ]
        """;
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, json);
        var client = BuildClient(handler);

        var result = await client.ListOpenIssuesByLabelAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Issues, Is.Not.Null);
        Assert.That(result.Issues!.Select(i => i.Number), Is.EqualTo(new[] { 42, 44 }),
            "GitHub's issues-list endpoint also returns pull requests — issue 43 (a pull_request entry) must be filtered out");
    }

    [Test]
    public async Task REQ904_ListOpenIssuesByLabelAsync_SendsBearerToken_AndFixedRepoAndLabel_NeverClientSupplied()
    {
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.OK, "[]");
        var client = BuildClient(handler, token: "a-fine-grained-pat");

        await client.ListOpenIssuesByLabelAsync(CancellationToken.None);

        Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.LastRequest.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(handler.LastRequest.Headers.Authorization!.Parameter, Is.EqualTo("a-fine-grained-pat"));
        Assert.That(handler.LastRequest.RequestUri!.ToString(), Is.EqualTo(
            "https://api.github.com/repos/johanpearson/xg-arcade/issues?state=open&labels=user-reported&per_page=100"),
            "the target repo, state=open, and the fixed user-reported label are the same server-configured values ADR-0064/GitHubIncidentReportOptions already uses — never a caller-supplied value");
    }

    [Test]
    public async Task REQ904_ListOpenIssuesByLabelAsync_ReturnsClientSafeFailure_NeverLeakingGitHubResponseBody_OnErrorStatus()
    {
        const string json = """{ "message": "API rate limit exceeded", "documentation_url": "https://docs.github.com" }""";
        var handler = FakeHttpMessageHandler.ReturningJson(HttpStatusCode.Forbidden, json);
        var client = BuildClient(handler);

        var result = await client.ListOpenIssuesByLabelAsync(CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Issues, Is.Null);
        Assert.That(result.FailureReason, Does.Not.Contain("API rate limit exceeded"),
            "ADR-0066: GitHub's own error detail must never reach the client");
    }

    [Test]
    public async Task REQ904_ListOpenIssuesByLabelAsync_ReturnsFailure_WhenGitHubIsUnreachable()
    {
        var handler = new FakeHttpMessageHandlerThrowingNetworkFailure();
        var client = BuildClient(handler);

        var result = await client.ListOpenIssuesByLabelAsync(CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Issues, Is.Null);
    }
}

// A minimal handler that always throws HttpRequestException, standing in
// for a genuine network failure (DNS/connection refused/etc.) —
// FakeHttpMessageHandler's own factory methods only cover a real HTTP
// response (success or error status), not a transport-level failure.
internal sealed class FakeHttpMessageHandlerThrowingNetworkFailure : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("simulated network failure");
}
