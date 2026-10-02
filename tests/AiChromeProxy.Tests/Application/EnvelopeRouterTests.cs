using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Application;

public sealed class EnvelopeRouterTests
{
	[Fact]
	public async Task KnownType_CallsHandler_ReturnsReply()
	{
		var router = new EnvelopeRouter([new EchoHandler("echo")]);

		var reply = await router.RouteAsync(Envelope.Create("echo", new { v = 1 }, "c1"), TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal("echo-reply", reply.Type);
		Assert.Equal("c1", reply.CorrelationId);
	}

	[Fact]
	public async Task UnknownType_ReturnsErrorWithSameCorrelationId()
	{
		var router = new EnvelopeRouter([]);

		var reply = await router.RouteAsync(Envelope.Create("nope", new { }, "c2"), TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Error, reply.Type);
		Assert.Equal("c2", reply.CorrelationId);
		Assert.Equal("unknown_type", reply.Payload.GetProperty("code").GetString());
		Assert.Equal("nope", reply.Payload.GetProperty("type").GetString());
	}

	[Fact]
	public void DuplicateHandlerType_Throws()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => new EnvelopeRouter([new EchoHandler("x"), new EchoHandler("x")]));

		Assert.Contains("'x'", ex.Message);
	}

	private sealed class EchoHandler(string type) : IEnvelopeHandler
	{
		public string Type => type;

		public Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct) =>
			Task.FromResult<Envelope?>(Envelope.Create(type + "-reply", new { }, request.CorrelationId));
	}
}
