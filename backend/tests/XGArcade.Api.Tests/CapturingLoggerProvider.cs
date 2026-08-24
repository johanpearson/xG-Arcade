using Microsoft.Extensions.Logging;

namespace XGArcade.Api.Tests;

// Captures every ILogger<T> entry written through this provider during a
// request, so a test can assert something was actually logged server-side
// (docs/coding-guidelines.md: "log the full exception server-side"), not
// just that the client got a Problem response.
//
// Extracted per quality-architect's own code-health-budget review of the
// REQ-513 diff (GitHub issue #239): this was a byte-for-byte-identical
// private nested class in three files (AdminEndpointTests, GridEndpointTests,
// AdminSuggestionEndpointTests) — a third copy of a shape existing twice
// already, which docs/coding-guidelines.md's rule-of-three requires
// extracting in the same diff that introduces it. Pure extraction, no
// behavior change: register with `logging.AddProvider(new CapturingLoggerProvider())`
// as before.
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Add((logLevel, formatter(state, exception)));
    }
}
