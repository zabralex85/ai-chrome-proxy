namespace AiChromeProxy.Domain;

/// <summary>Payload of <see cref="MessageTypes.Error"/>: <c>{code, message?, type?}</c>; <c>type</c> is set only for <see cref="ErrorCodes.UnknownType"/>.</summary>
public sealed record ErrorPayload(string Code, string? Message = null, string? Type = null);
