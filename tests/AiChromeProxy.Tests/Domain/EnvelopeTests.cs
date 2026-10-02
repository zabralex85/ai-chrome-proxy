using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Domain;

public sealed class EnvelopeTests
{
	[Fact]
	public void Create_SerializesPayloadAndKeepsCorrelationId()
	{
		var envelope = Envelope.Create("t", new { a = 1 }, "c");

		Assert.Equal("t", envelope.Type);
		Assert.Equal(1, envelope.Payload.GetProperty("a").GetInt32());
		Assert.Equal("c", envelope.CorrelationId);
	}

	[Fact]
	public void Create_WithoutCorrelationId_LeavesItNull()
	{
		Assert.Null(Envelope.Create("t", new { }).CorrelationId);
	}

	[Fact]
	public void Create_PayloadWireShapeIsCamelCase()
	{
		var envelope = Envelope.Create("t", new WireDto("a.cs", 3));

		Assert.Equal("{\"filePath\":\"a.cs\",\"lineCount\":3}", envelope.Payload.GetRawText());
	}

	private sealed record WireDto(string FilePath, int LineCount);
}
