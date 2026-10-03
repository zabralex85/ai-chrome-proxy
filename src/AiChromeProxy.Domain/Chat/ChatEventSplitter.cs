using System.Text;
using System.Text.Json;

namespace AiChromeProxy.Domain.Chat;

/// <summary>Makes an event fit <see cref="ChatLimits"/>.</summary>
public static class ChatEventSplitter
{
	private const string Ellipsis = "…";

	/// <summary>
	/// Truncates <c>summary</c> to <see cref="ChatLimits.ToolSummaryBytes"/> UTF-8 bytes (ending in an ellipsis) and splits a long
	/// <c>prompt</c>/<c>text</c>/<c>message</c> into parts that each serialize to at most <paramref name="maxBytes"/> bytes;
	/// parts keep the original <c>Seq</c> (the caller numbers them) and never cut a surrogate pair.
	/// </summary>
	/// <param name="value">The event.</param>
	/// <param name="maxBytes">Largest serialized part; below <see cref="ChatLimits.MaxEventBytes"/> when the event must also fit a page with others' fields.</param>
	public static IReadOnlyList<ChatEvent> Split(ChatEvent value, int maxBytes = ChatLimits.MaxEventBytes)
	{
		var e = value.Summary is null ? value : value with { Summary = Truncate(value.Summary) };
		var splittable = e.Kind is ChatEventKinds.Prompt or ChatEventKinds.Text or ChatEventKinds.Message;
		if (!splittable || e.Text is null || (e.Text.Length <= ChatLimits.MaxTextChars && Size(e) <= maxBytes))
		{
			return [e];
		}

		var parts = new List<ChatEvent>();
		var text = e.Text;
		var start = 0;
		while (start < text.Length)
		{
			var length = Math.Min(text.Length - start, ChatLimits.MaxTextChars);
			while (true)
			{
				if (length < text.Length - start && length > 1 && char.IsHighSurrogate(text[start + length - 1]))
				{
					length--;
				}

				var excess = Size(e with { Text = text.Substring(start, length) }) - maxBytes;
				if (excess <= 0 || length <= 2)
				{
					break;
				}

				// Escaped characters take up to 12 bytes per UTF-16 pair; shrink by a safe estimate and retry.
				length = Math.Max(2, length - (excess / 12) - 1);
			}

			parts.Add(e with { Text = text.Substring(start, length) });
			start += length;
		}

		return parts;
	}

	/// <summary>
	/// <paramref name="s"/> cut to at most <paramref name="maxBytes"/> UTF-8 bytes, ending in "…" when cut; never cuts a surrogate pair.
	/// Used for summaries and error texts.
	/// </summary>
	public static string Truncate(string s, int maxBytes = ChatLimits.ToolSummaryBytes)
	{
		if (Encoding.UTF8.GetByteCount(s) <= maxBytes)
		{
			return s;
		}

		var budget = maxBytes - Encoding.UTF8.GetByteCount(Ellipsis);
		var end = 0;
		var bytes = 0;
		while (end < s.Length)
		{
			var step = char.IsHighSurrogate(s[end]) && end + 1 < s.Length ? 2 : 1;
			var n = Encoding.UTF8.GetByteCount(s.AsSpan(end, step));
			if (bytes + n > budget)
			{
				break;
			}

			bytes += n;
			end += step;
		}

		return string.Concat(s.AsSpan(0, end), Ellipsis);
	}

	private static int Size(ChatEvent e) => JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length;
}
