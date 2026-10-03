namespace AiChromeProxy.Client.Transport;

/// <summary>The server answered a request with <c>error {code, message?}</c>.</summary>
public sealed class RequestFailedException(string code, string? message) : Exception(message ?? code)
{
	/// <summary>One of <see cref="AiChromeProxy.Domain.ErrorCodes"/>.</summary>
	public string Code => code;
}
