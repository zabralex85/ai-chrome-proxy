namespace AiChromeProxy.Application.Transport;

/// <summary>Thrown by a handler to answer with <c>error {code, message}</c>. The message reaches the client: no secrets, no file contents.</summary>
public sealed class EnvelopeException(string code, string message) : Exception(message)
{
	/// <summary>One of <see cref="AiChromeProxy.Domain.ErrorCodes"/>.</summary>
	public string Code => code;
}
