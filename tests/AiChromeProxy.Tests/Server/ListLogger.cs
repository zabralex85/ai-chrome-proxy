using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Server;

/// <summary>Records formatted log messages; thread-safe (background services log from the thread pool).</summary>
public sealed class ListLogger<T> : ILogger<T>
{
	private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

	public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

	public IReadOnlyList<string> Messages => [.. _entries.Select(e => e.Message)];

	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
		_entries.Enqueue((logLevel, formatter(state, exception)));
}
