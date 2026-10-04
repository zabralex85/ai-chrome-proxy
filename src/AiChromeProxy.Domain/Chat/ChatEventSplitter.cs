using System.Text;
using System.Text.Json;

namespace AiChromeProxy.Domain.Chat;

/// <summary>Makes an event fit <see cref="ChatLimits"/>.</summary>
public static class ChatEventSplitter
{
	private const string Ellipsis = "…";

	/// <summary>
	/// Truncates <c>summary</c> to <see cref="ChatLimits.ToolSummaryBytes"/> UTF-8 bytes (ending in an ellipsis) — a <c>permission</c>'s
	/// to <see cref="ChatLimits.PermissionSummaryBytes"/> serialized, marking it <c>truncated</c> when cut — and <c>name</c> to
	/// <see cref="ChatLimits.MaxNameBytes"/>, and splits a long <c>prompt</c>/<c>text</c>/<c>message</c> into parts that each serialize
	/// to at most <paramref name="maxBytes"/> bytes; parts keep the original <c>Seq</c> (the caller numbers them) and never cut a surrogate pair.
	/// </summary>
	/// <param name="value">The event.</param>
	/// <param name="maxBytes">Largest serialized part; below <see cref="ChatLimits.MaxEventBytes"/> when the event must also fit a page with others' fields.</param>
	public static IReadOnlyList<ChatEvent> Split(ChatEvent value, int maxBytes = ChatLimits.MaxEventBytes)
	{
		var permission = value.Kind == ChatEventKinds.Permission;
		var name = value.Name is null ? null : Truncate(value.Name, ChatLimits.MaxNameBytes);
		var summary = value.Summary is null ? null : permission ? FitJson(value.Summary, ChatLimits.PermissionSummaryBytes) : Truncate(value.Summary);
		var cut = !ReferenceEquals(summary, value.Summary);
		var e = !cut && ReferenceEquals(name, value.Name)
			? value
			: value with { Name = name, Summary = summary, Truncated = permission && cut ? true : value.Truncated };
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

	/// <summary>
	/// <paramref name="s"/> cut so that it serializes as a JSON string (<see cref="JsonSerializerOptions.Web"/>, which escapes non-ASCII and
	/// quotes) to at most <paramref name="maxBytes"/> bytes, ending in "…" when cut; the same instance when it fits. Never cuts a surrogate pair.
	/// </summary>
	public static string FitJson(string s, int maxBytes)
	{
		if (JsonSize(s) <= maxBytes)
		{
			return s;
		}

		// The longest prefix that still fits, by binary search: the size grows with the prefix.
		var (lo, hi) = (0, s.Length);
		while (lo < hi)
		{
			var mid = (lo + hi + 1) / 2;
			(lo, hi) = JsonSize(Cut(s, mid)) <= maxBytes ? (mid, hi) : (lo, mid - 1);
		}

		return Cut(s, lo);
	}

	private static string Cut(string s, int end) =>
		string.Concat(s.AsSpan(0, end > 0 && char.IsHighSurrogate(s[end - 1]) ? end - 1 : end), Ellipsis);

	private static int JsonSize(string s) => JsonSerializer.SerializeToUtf8Bytes(s, JsonSerializerOptions.Web).Length;

	private static int Size(ChatEvent e) => JsonSerializer.SerializeToUtf8Bytes(e, JsonSerializerOptions.Web).Length;
}
