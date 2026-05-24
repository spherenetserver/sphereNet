using Microsoft.Extensions.Logging;

namespace SphereNet.Tests;

internal sealed class ScriptDiagnosticCollector
{
    private readonly List<ScriptDiagnostic> _entries = [];

    public IReadOnlyList<ScriptDiagnostic> Entries => _entries;

    public void Add(string category, string message)
    {
        _entries.Add(new ScriptDiagnostic(category, message));
    }

    public void AddUnresolved(string message) => Add("expr", message);

    public int Count(string category) =>
        _entries.Count(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<(string Message, int Count)> Top(string category, int take = 20) =>
        _entries
            .Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Message, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(g => (g.Key, g.Count()))
            .ToArray();
}

internal readonly record struct ScriptDiagnostic(string Category, string Message);

internal sealed class CollectingLoggerProvider : ILoggerProvider
{
    private readonly ScriptDiagnosticCollector _collector;

    public CollectingLoggerProvider(ScriptDiagnosticCollector collector)
    {
        _collector = collector;
    }

    public ILogger CreateLogger(string categoryName) => new CollectingLogger(_collector, categoryName);

    public void Dispose()
    {
    }

    private sealed class CollectingLogger : ILogger
    {
        private readonly ScriptDiagnosticCollector _collector;
        private readonly string _categoryName;

        public CollectingLogger(ScriptDiagnosticCollector collector, string categoryName)
        {
            _collector = collector;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            string category = message.StartsWith("Unhandled script line", StringComparison.OrdinalIgnoreCase)
                ? "unhandled"
                : message.StartsWith("Function not found", StringComparison.OrdinalIgnoreCase)
                    ? "missing-function"
                    : message.StartsWith("Unknown section", StringComparison.OrdinalIgnoreCase)
                        ? "unknown-section"
                        : "log";

            _collector.Add(category, $"{_categoryName}: {message}");
        }
    }
}
