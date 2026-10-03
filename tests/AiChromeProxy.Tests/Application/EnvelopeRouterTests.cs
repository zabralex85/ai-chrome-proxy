using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Application;

public sealed class EnvelopeRouterTests
{
	private static readonly EnvelopeContext Context = new("conn-1", "user@example.com", (_, _) => Task.CompletedTask);

	[Fact]
	public async Task KnownType_CallsHandlerWithContext_ReturnsReply()
	{
		var handler = new EchoHandler("echo");
		var router = new EnvelopeRouter([handler]);

		var reply = await router.RouteAsync(Envelope.Create("echo", new { v = 1 }, "c1"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal("echo-reply", reply.Type);
		Assert.Equal("c1", reply.CorrelationId);
		Assert.Same(Context, handler.LastContext);
	}

	[Fact]
	public async Task UnknownType_ReturnsErrorWithSameCorrelationId()
	{
		var router = new EnvelopeRouter([]);

		var reply = await router.RouteAsync(Envelope.Create("nope", new { }, "c2"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Error, reply.Type);
		Assert.Equal("c2", reply.CorrelationId);
		Assert.Equal("unknown_type", reply.Payload.GetProperty("code").GetString());
		Assert.Equal("nope", reply.Payload.GetProperty("type").GetString());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public async Task MissingType_BadRequest(string? type)
	{
		var router = new EnvelopeRouter([]);

		var reply = await router.RouteAsync(new Envelope(type!, JsonSerializer.SerializeToElement(new { }), "c3"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Error, reply.Type);
		Assert.Equal("c3", reply.CorrelationId);
		Assert.Equal(ErrorCodes.BadRequest, reply.Payload.GetProperty("code").GetString());
	}

	[Fact]
	public async Task HandlerThrowsEnvelopeException_ErrorWithCodeAndMessage()
	{
		var router = new EnvelopeRouter([new ThrowingHandler("t", new EnvelopeException(ErrorCodes.NotFound, "no such file"))]);

		var reply = await router.RouteAsync(Envelope.Create("t", new { }, "c4"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal("c4", reply.CorrelationId);
		var error = reply.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web);
		Assert.Equal(new ErrorPayload(ErrorCodes.NotFound, "no such file"), error);
	}

	[Fact]
	public async Task HandlerThrowsOtherException_Propagates()
	{
		var router = new EnvelopeRouter([new ThrowingHandler("t", new InvalidOperationException("boom"))]);

		await Assert.ThrowsAsync<InvalidOperationException>(() => router.RouteAsync(Envelope.Create("t", new { }), Context, TestContext.Current.CancellationToken));
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

		public EnvelopeContext? LastContext { get; private set; }

		public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
		{
			LastContext = context;
			return Task.FromResult<Envelope?>(Envelope.Create(type + "-reply", new { }, request.CorrelationId));
		}
	}

	private sealed class ThrowingHandler(string type, Exception exception) : IEnvelopeHandler
	{
		public string Type => type;

		public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct) => throw exception;
	}
}
