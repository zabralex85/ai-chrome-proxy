using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using BenchmarkDotNet.Attributes;

namespace AiChromeProxy.Benchmarks;

[MemoryDiagnoser]
public class RouterBenchmarks
{
	private static readonly Envelope Ping = Envelope.Create(MessageTypes.Ping, new { }, "c1");
	private static readonly Envelope Unknown = Envelope.Create("unknown", new { }, "c2");

	private readonly EnvelopeRouter _router = new([new PingHandler(TimeProvider.System)]);

	[Benchmark]
	public Task<Envelope?> RoutePingAsync() => _router.RouteAsync(Ping, CancellationToken.None);

	[Benchmark]
	public Task<Envelope?> RouteUnknownAsync() => _router.RouteAsync(Unknown, CancellationToken.None);
}
