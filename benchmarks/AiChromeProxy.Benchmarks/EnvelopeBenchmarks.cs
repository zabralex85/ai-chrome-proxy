using System.Text.Json;
using AiChromeProxy.Domain;
using BenchmarkDotNet.Attributes;

namespace AiChromeProxy.Benchmarks;

[MemoryDiagnoser]
public class EnvelopeBenchmarks
{
	private static readonly SampleDto Dto = new("src/AiChromeProxy.Server/Program.cs", 42);

	private readonly Envelope _envelope = Envelope.Create("sample", Dto, "c1");

	[Benchmark]
	public Envelope Create() => Envelope.Create("sample", Dto, "c1");

	[Benchmark]
	public string Serialize() => JsonSerializer.Serialize(_envelope, JsonSerializerOptions.Web);

	public sealed record SampleDto(string FilePath, int LineCount);
}
