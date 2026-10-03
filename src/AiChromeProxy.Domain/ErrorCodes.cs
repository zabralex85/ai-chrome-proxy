namespace AiChromeProxy.Domain;

/// <summary>Values of <c>code</c> in an <see cref="MessageTypes.Error"/> payload.</summary>
public static class ErrorCodes
{
	/// <summary>Malformed envelope or payload (e.g. null or empty <c>type</c>, invalid path).</summary>
	public const string BadRequest = "bad_request";

	/// <summary>No handler for the envelope's <c>type</c>; the payload carries <c>type</c>.</summary>
	public const string UnknownType = "unknown_type";

	public const string NotFound = "not_found";

	public const string TooLarge = "too_large";

	/// <summary>The handler failed unexpectedly; details are only in the server log.</summary>
	public const string Internal = "internal";
}
