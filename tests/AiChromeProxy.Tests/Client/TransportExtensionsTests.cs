using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Client;

public sealed class TransportExtensionsTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task Request_ReturnsTheReplyWithItsCorrelationId_IgnoresOthers()
	{
		var transport = new FakeTransport();
		transport.Reply = e =>
		{
			transport.Push(Envelope.Create("noise", new { }, "someone-else"));
			return Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Pong, new { }, e.CorrelationId));
		};

		var reply = await transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }, "ignored"), Timeout, TestContext.Current.CancellationToken);

		Assert.Equal(MessageTypes.Pong, reply.Type);
		var sent = Assert.Single(transport.Sent);
		Assert.NotNull(sent.CorrelationId);
		Assert.NotEqual("ignored", sent.CorrelationId);
		Assert.Equal(sent.CorrelationId, reply.CorrelationId);
		Assert.Equal(0, transport.ReceivedHandlers);
	}

	[Fact]
	public async Task Request_ErrorReply_ThrowsWithCodeAndMessage()
	{
		var transport = new FakeTransport
		{
			Reply = e => Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Error, new ErrorPayload(ErrorCodes.TooLarge, "file too large"), e.CorrelationId)),
		};

		var ex = await Assert.ThrowsAsync<RequestFailedException>(
			() => transport.RequestAsync(Envelope.Create("x", new { }), Timeout, TestContext.Current.CancellationToken));

		Assert.Equal(ErrorCodes.TooLarge, ex.Code);
		Assert.Equal("file too large", ex.Message);
		Assert.Equal(0, transport.ReceivedHandlers);
	}

	[Fact]
	public async Task Request_SendFails_Throws_AndUnsubscribes()
	{
		var transport = new FakeTransport { Reply = _ => Task.FromException<Envelope?>(new IOException("offline")) };

		await Assert.ThrowsAsync<IOException>(
			() => transport.RequestAsync(Envelope.Create("x", new { }), Timeout, TestContext.Current.CancellationToken));

		Assert.Equal(0, transport.ReceivedHandlers);
	}

	[Fact]
	public async Task Request_NoReply_TimesOut_AndUnsubscribes()
	{
		var transport = new FakeTransport();

		await Assert.ThrowsAsync<TimeoutException>(
			() => transport.RequestAsync(Envelope.Create("x", new { }), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

		Assert.Equal(0, transport.ReceivedHandlers);
	}
}
