// GPD Forge — a logger a test can read back. GPL-3.0-or-later.
//
// Several fixes are about what the SERVICE LOG says — a stalled sampler, a failed battery query —
// because on an installed handheld the log is the only place an operator looks. "It logs once per
// outage" is a behaviour, so it gets asserted like one.
using Microsoft.Extensions.Logging;

namespace GpdForge.Core.Tests;

public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public int Count(LogLevel level) => Entries.Count(e => e.Level == level);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
    }
}
