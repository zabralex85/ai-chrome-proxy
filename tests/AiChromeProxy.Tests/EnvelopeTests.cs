using AiChromeProxy.Shared;

namespace AiChromeProxy.Tests;

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
}
