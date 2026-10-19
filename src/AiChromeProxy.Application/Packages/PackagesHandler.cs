using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;

namespace AiChromeProxy.Application.Packages;

/// <summary>Routes one <c>packages.nuget.*</c> request type (see <see cref="Types"/>; registered once per type) to <see cref="PackageUploads"/>; only the last chunk of a package gets a reply.</summary>
public sealed class PackagesHandler(string type, PackageUploads uploads) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.PackagesNuGetHave, MessageTypes.PackagesNuGetBegin, MessageTypes.PackagesNuGetChunk];

	public string Type => type;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var reply = type switch
		{
			MessageTypes.PackagesNuGetHave => Envelope.Create(MessageTypes.PackagesNuGetMissing, uploads.Have(Read<NuGetHavePayload>(request)), request.CorrelationId),
			MessageTypes.PackagesNuGetBegin => Envelope.Create(MessageTypes.PackagesNuGetBegun, uploads.Begin(context.ConnectionId, Read<NuGetBeginPayload>(request)), request.CorrelationId),
			_ => uploads.Chunk(context.ConnectionId, Read<NuGetChunkPayload>(request)) is { } stored
				? Envelope.Create(MessageTypes.PackagesNuGetStored, stored, request.CorrelationId)
				: null,
		};

		return Task.FromResult(reply);
	}

	private static T Read<T>(Envelope request)
		where T : class
	{
		try
		{
			return request.Payload.Deserialize<T>(JsonSerializerOptions.Web) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Invalid {request.Type} payload.");
		}
	}
}
