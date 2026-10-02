using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Hosting;
using AiChromeProxy.Tray.Clef;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Tray;

public sealed class ClefTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));

	public ClefTests() => Directory.CreateDirectory(_dir);

	[Fact]
	public void Parse_RenderedLine()
	{
		var entry = ClefParser.Parse("""{"@t":"2026-10-02T10:00:00.1234567Z","@m":"Now listening on: \"http://127.0.0.1:5180\"","@i":"a1b2c3d4","Address":"http://127.0.0.1:5180"}""");

		Assert.NotNull(entry);
		Assert.Equal(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567), entry.Timestamp);
		Assert.Equal(ClefLevel.Information, entry.Level);
		Assert.Equal("Now listening on: \"http://127.0.0.1:5180\"", entry.Message);
		Assert.Null(entry.Exception);
	}

	[Fact]
	public void Parse_LevelAndException()
	{
		var entry = ClefParser.Parse("""{"@t":"2026-10-02T10:00:00Z","@m":"Boom","@l":"Error","@x":"System.Exception: boom\r\n   at X"}""");

		Assert.Equal(ClefLevel.Error, entry?.Level);
		Assert.Equal("System.Exception: boom\r\n   at X", entry?.Exception);
	}

	[Fact]
	public void Parse_TemplateOnly_FallsBackToTemplate()
	{
		Assert.Equal("Hello {Name}", ClefParser.Parse("""{"@t":"2026-10-02T10:00:00Z","@mt":"Hello {Name}","Name":"x"}""")?.Message);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("{ not json")]
	[InlineData("[1,2]")]
	[InlineData("""{"@m":"no timestamp"}""")]
	[InlineData("""{"@t":42,"@m":"numeric timestamp"}""")]
	[InlineData("""{"@t":"yesterday","@m":"bad timestamp"}""")]
	public void Parse_Unusable_Null(string line)
	{
		Assert.Null(ClefParser.Parse(line));
	}

	[Fact]
	public void Parse_UnknownLevel_Information()
	{
		Assert.Equal(ClefLevel.Information, ClefParser.Parse("""{"@t":"2026-10-02T10:00:00Z","@m":"x","@l":"Chatty"}""")?.Level);
	}

	[Theory]
	[InlineData(ClefLevel.Warning, null, true)]
	[InlineData(ClefLevel.Warning, "", true)]
	[InlineData(ClefLevel.Error, null, false)]
	[InlineData(ClefLevel.Verbose, "DISK", true)]
	[InlineData(ClefLevel.Verbose, " ioexception ", true)]
	[InlineData(ClefLevel.Verbose, "network", false)]
	public void Matches_LevelAndText(ClefLevel minimum, string? text, bool expected)
	{
		var entry = new LogEntry(DateTimeOffset.UnixEpoch, ClefLevel.Warning, "Disk almost full", "System.IO.IOException: no space");

		Assert.Equal(expected, entry.Matches(minimum, text));
	}

	[Fact]
	public void Tail_ReadsCompleteLinesOnly_ThenTheRest()
	{
		var path = Path.Combine(_dir, "server-20261002.clef");
		File.WriteAllText(path, Line("one") + Line("two") + """{"@t":"2026-10-02T10:00:00Z","@m":"thr""");
		var tail = new ClefTail(path);

		Assert.Equal(["one", "two"], tail.ReadNew().Select(e => e.Message));
		Assert.Empty(tail.ReadNew());

		File.AppendAllText(path, "ee\"}\n" + Line("four"));
		Assert.Equal(["three", "four"], tail.ReadNew().Select(e => e.Message));
	}

	[Fact]
	public void Tail_CrLfAndGarbageLines_Skipped()
	{
		var path = Path.Combine(_dir, "server-20261002.clef");
		File.WriteAllText(path, Line("one").Replace("\n", "\r\n") + "garbage\n\n" + Line("two"));

		Assert.Equal(["one", "two"], new ClefTail(path).ReadNew().Select(e => e.Message));
	}

	[Fact]
	public void Tail_WhileServerSerilogSinkHoldsTheFile_SharedRead()
	{
		var dataDir = new DataDirectory(_dir);
		using var provider = new ServiceCollection().AddServerLogging(new ConfigurationBuilder().Build(), dataDir).BuildServiceProvider();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
		logger.LogWarning("first {N}", 1);
		var tail = new ClefTail(Assert.Single(Directory.GetFiles(dataDir.Logs)));

		var first = Assert.Single(tail.ReadNew());
		logger.LogError(new InvalidOperationException("bad"), "second {N}", 2);
		var second = Assert.Single(tail.ReadNew());

		Assert.Equal((ClefLevel.Warning, "first 1"), (first.Level, first.Message));
		Assert.Equal((ClefLevel.Error, "second 2"), (second.Level, second.Message));
		Assert.Contains("InvalidOperationException: bad", second.Exception);
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private static string Line(string message) => $$"""{"@t":"2026-10-02T10:00:00Z","@m":"{{message}}"}""" + "\n";
}
