using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Application;

public sealed class PingHandlerTests
{
	[Fact]
	public async Task Ping_ReturnsPongWithUtcServerTime()
	{
		var now = new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero);
		var handler = new PingHandler(new FixedTimeProvider(now));

		var reply = await handler.HandleAsync(Envelope.Create(MessageTypes.Ping, new { }, "p1"), TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Pong, reply.Type);
		Assert.Equal("p1", reply.CorrelationId);
		var serverTime = DateTimeOffset.Parse(reply.Payload.GetProperty("serverTime").GetString()!);
		Assert.Equal(now, serverTime);
		Assert.Equal(TimeSpan.Zero, serverTime.Offset);
	}
}
